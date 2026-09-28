namespace Rdpeek.AgentService;

/// <summary>
/// The orchestration half of the service. Owns the set of agents-per-session, decides
/// when to (re)launch, and applies backoff on crash loops. All P/Invoke lives in
/// <see cref="SessionLauncher"/>; the pure restart math lives in <see cref="BackoffPolicy"/>.
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
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _stopping;

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

        lock (_gate)
        {
            if (_agents.ContainsKey(sessionId))
                return; // already running / tracked
        }

        LaunchedAgent agent;
        try
        {
            agent = SessionLauncher.Launch(sessionId, _agentExePath);
        }
        catch (Exception ex)
        {
            _log($"Launch into session {sessionId} ({reason}) failed: {ex.Message}");
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

        _log($"Launched agent pid {agent.ProcessId} into session {sessionId} ({reason}).");

        // Watch for exit and decide whether to relaunch.
        _ = agent.ExitTask.ContinueWith(_ => OnAgentExited(sessionId, agent),
            TaskScheduler.Default);
    }

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

        // Session gone? Don't relaunch — a logoff notification may not have arrived yet.
        bool stillActive = SessionLauncher.EnumerateActiveUserSessions().Any(s => s.SessionId == sessionId);
        if (!stillActive)
        {
            _log($"Agent for session {sessionId} exited; session no longer active — not relaunching.");
            lock (_gate) _failureStreak.Remove(sessionId);
            SessionLauncher.RemoveTask(sessionId);
            return;
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

        string exit = agent.ExitCode is uint c ? $"exit 0x{c:X8}" : "exit code unknown";
        _log($"Agent for session {sessionId} exited after {ranFor.TotalSeconds:F0}s ({exit}) " +
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
