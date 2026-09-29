namespace Rdpeek.AgentService;

/// <summary>
/// The orchestration half of the service. Owns the set of agents-per-session, decides
/// when to (re)launch, and applies backoff on crash loops. All P/Invoke lives in
/// <see cref="SessionLauncher"/>; the pure restart math lives in <see cref="BackoffPolicy"/>.
///
/// Launch strategy per session: try native <see cref="SessionLauncher.LaunchNative"/> first;
/// if it can't start, or the agent dies immediately several times, switch that session to the
/// <see cref="SessionLauncher.LaunchViaTask"/> fallback. A fresh logon starts on native again.
///
/// Session-0 reminder: this runs in session 0 and can't touch the DVC. It only shepherds
/// in-session agent processes, one per active user session.
/// </summary>
internal sealed class AgentSupervisor : IDisposable
{
    private readonly string _agentExePath;
    private readonly Action<string> _log;
    private readonly BackoffPolicy _backoff;

    private readonly object _gate = new();
    private readonly Dictionary<uint, LaunchedAgent> _agents = new();
    private readonly Dictionary<uint, int> _failureStreak = new();
    // Per-session launch strategy: native CreateProcessAsUser by default, Task Scheduler as a
    // fallback we switch to only if native keeps dying immediately (or can't even start).
    private readonly Dictionary<uint, LaunchMethod> _method = new();
    private readonly Dictionary<uint, int> _nativeFastFails = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _stopping;

    private enum LaunchMethod { Native, Task }

    // A native launch that exits faster than this, repeatedly, is treated as broken (e.g. the
    // 0xC0000142 desktop-attach failure) and the session is switched to the Task launcher.
    private static readonly TimeSpan NativeFastFail = TimeSpan.FromSeconds(8);
    private const int NativeFastFailsBeforeFallback = 2;

    public AgentSupervisor(string agentExePath, Action<string> log, BackoffPolicy? backoff = null)
    {
        _agentExePath = agentExePath;
        _log = log;
        _backoff = backoff ?? new BackoffPolicy();
    }

    /// <summary>Launch agents into every currently-active user session.</summary>
    public void Start()
    {
        _log($"Supervisor starting. Agent exe: {_agentExePath}");
        foreach (var s in SessionLauncher.EnumerateActiveUserSessions())
            EnsureAgent(s.SessionId, "startup");
    }

    /// <summary>A session became usable (logon / RDP connect) — make sure it has an agent.</summary>
    public void OnSessionActive(uint sessionId) => EnsureAgent(sessionId, "session-active");

    /// <summary>A session went away (logoff / disconnect) — stop tracking and terminate its agent.</summary>
    public void OnSessionInactive(uint sessionId)
    {
        LaunchedAgent? agent = null;
        lock (_gate)
        {
            if (_agents.Remove(sessionId, out var a)) agent = a;
            _failureStreak.Remove(sessionId);
            _method.Remove(sessionId);          // a fresh logon starts on native again
            _nativeFastFails.Remove(sessionId);
        }
        if (agent is not null)
        {
            _log($"Session {sessionId} inactive; terminating agent pid {agent.ProcessId}.");
            agent.Terminate();
            agent.Dispose();
        }
        // Remove the per-session task whether or not we were tracking a process for it.
        SessionLauncher.RemoveTask(sessionId);
    }

    /// <summary>Stop everything and terminate all agents this service started.</summary>
    public void Stop()
    {
        _stopping = true;
        _cts.Cancel();
        List<LaunchedAgent> toKill;
        lock (_gate)
        {
            toKill = _agents.Values.ToList();
            _agents.Clear();
            _failureStreak.Clear();
            _method.Clear();
            _nativeFastFails.Clear();
        }
        foreach (var a in toKill)
        {
            _log($"Service stopping; terminating agent pid {a.ProcessId} (session {a.SessionId}).");
            a.Terminate();
            a.Dispose();
            SessionLauncher.RemoveTask(a.SessionId);
        }
    }

    /// <summary>Launch an agent for the session if one isn't already tracked.</summary>
    private void EnsureAgent(uint sessionId, string reason)
    {
        if (_stopping || sessionId == 0) return;

        LaunchMethod method;
        lock (_gate)
        {
            if (_agents.ContainsKey(sessionId))
                return; // already running / tracked
            method = _method.GetValueOrDefault(sessionId, LaunchMethod.Native);
        }

        LaunchedAgent agent;
        try
        {
            agent = DoLaunch(sessionId, method);
        }
        catch (Exception ex) when (method == LaunchMethod.Native)
        {
            // Native couldn't even start (no winlogon, OpenProcess denied, ...) — fall back to Task.
            _log($"Native launch into session {sessionId} ({reason}) failed: {ex.Message}. Falling back to scheduled task.");
            lock (_gate) _method[sessionId] = LaunchMethod.Task;
            method = LaunchMethod.Task;
            try { agent = DoLaunch(sessionId, method); }
            catch (Exception ex2)
            {
                _log($"Task launch into session {sessionId} ({reason}) also failed: {ex2.Message}");
                return;
            }
        }
        catch (Exception ex)
        {
            _log($"Launch ({method}) into session {sessionId} ({reason}) failed: {ex.Message}");
            return;
        }

        bool added;
        lock (_gate)
        {
            // Another thread may have launched concurrently; if so, kill the loser.
            added = _agents.TryAdd(sessionId, agent);
        }
        if (!added)
        {
            agent.Terminate();
            agent.Dispose();
            return;
        }

        _log($"Launched agent pid {agent.ProcessId} into session {sessionId} ({reason}, {method}).");

        // Watch for exit and decide whether to relaunch.
        _ = agent.ExitTask.ContinueWith(_ => OnAgentExited(sessionId, agent),
            TaskScheduler.Default);
    }

    private LaunchedAgent DoLaunch(uint sessionId, LaunchMethod method)
        => method == LaunchMethod.Native
            ? SessionLauncher.LaunchNative(sessionId, _agentExePath)
            : SessionLauncher.LaunchViaTask(sessionId, _agentExePath);

    /// <summary>An agent exited: relaunch it (with backoff) if the session is still active.</summary>
    private void OnAgentExited(uint sessionId, LaunchedAgent agent)
    {
        var ranFor = DateTime.UtcNow - agent.StartedUtc;

        lock (_gate)
        {
            // Only act if THIS agent is still the tracked one (guards against a
            // logoff/terminate that already removed & replaced it).
            if (!_agents.TryGetValue(sessionId, out var tracked) || !ReferenceEquals(tracked, agent))
            {
                agent.Dispose();
                return;
            }
            _agents.Remove(sessionId);
        }
        agent.Dispose();

        if (_stopping) return;
        string exit = agent.ExitCode is uint c ? $"exit 0x{c:X8}" : "exit code unknown";

        // Session gone? Don't relaunch — a logoff notification may not have arrived yet.
        bool stillActive = SessionLauncher.EnumerateActiveUserSessions().Any(s => s.SessionId == sessionId);
        if (!stillActive)
        {
            _log($"Agent for session {sessionId} exited; session no longer active — not relaunching.");
            lock (_gate) { _failureStreak.Remove(sessionId); _nativeFastFails.Remove(sessionId); }
            SessionLauncher.RemoveTask(sessionId);
            return;
        }

        // If a NATIVE launch keeps dying immediately, switch this session to the Task fallback.
        LaunchMethod method;
        lock (_gate) method = _method.GetValueOrDefault(sessionId, LaunchMethod.Native);
        if (method == LaunchMethod.Native)
        {
            if (ranFor < NativeFastFail)
            {
                int fails;
                lock (_gate) { _nativeFastFails.TryGetValue(sessionId, out fails); fails++; _nativeFastFails[sessionId] = fails; }
                if (fails >= NativeFastFailsBeforeFallback)
                {
                    lock (_gate) { _method[sessionId] = LaunchMethod.Task; _failureStreak.Remove(sessionId); _nativeFastFails.Remove(sessionId); }
                    _log($"Native launch for session {sessionId} died fast {fails}x ({exit}); " +
                         "falling back to the scheduled-task launcher.");
                    _ = RelaunchAfterAsync(sessionId, TimeSpan.Zero);
                    return;
                }
            }
            else
            {
                // A healthy native run clears the fast-fail streak.
                lock (_gate) _nativeFastFails.Remove(sessionId);
            }
        }

        int streak;
        lock (_gate)
        {
            _failureStreak.TryGetValue(sessionId, out streak);
        }
        var (nextStreak, delay) = _backoff.NextDelay(streak, ranFor);
        lock (_gate)
        {
            _failureStreak[sessionId] = nextStreak;
        }

        _log($"Agent for session {sessionId} exited after {ranFor.TotalSeconds:F0}s ({exit}, {method}) " +
             $"(failure #{nextStreak}); relaunching in {delay.TotalSeconds:F0}s.");

        _ = RelaunchAfterAsync(sessionId, delay);
    }

    private async Task RelaunchAfterAsync(uint sessionId, TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // service stopping
        }
        if (_stopping) return;

        // Re-check the session is still active right before relaunching.
        if (SessionLauncher.EnumerateActiveUserSessions().Any(s => s.SessionId == sessionId))
            EnsureAgent(sessionId, "relaunch");
        else
            lock (_gate) _failureStreak.Remove(sessionId);
    }

    public void Dispose()
    {
        _cts.Dispose();
    }
}
