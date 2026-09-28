using System.ServiceProcess;

namespace Rdpeek.AgentService;

/// <summary>
/// Classic <see cref="ServiceBase"/> host for the supervisor. We use ServiceBase directly
/// (rather than the generic host's WindowsServiceLifetime) for one concrete reason: we need
/// <see cref="OnSessionChange"/> / SERVICE_CONTROL_SESSIONCHANGE, which the generic host does
/// not surface. That keeps the dependency footprint to a single NuGet package.
/// </summary>
internal sealed class AgentSupervisorService : ServiceBase
{
    internal const string ServiceNameConst = "RdpeekAgentSvc";

    private AgentSupervisor? _supervisor;

    public AgentSupervisorService()
    {
        ServiceName = ServiceNameConst;
        CanStop = true;
        CanShutdown = true;
        // Required to receive WTS_SESSION_LOGON / WTS_REMOTE_CONNECT / *_LOGOFF / *_DISCONNECT.
        CanHandleSessionChangeEvent = true;
        AutoLog = true; // start/stop entries in the Application event log
    }

    protected override void OnStart(string[] args)
    {
        string serviceDir = AppContext.BaseDirectory;
        string? agentExe = AgentPathResolver.ResolveLive(serviceDir);
        if (agentExe is null)
        {
            var tried = string.Join("; ", AgentPathResolver.EnumerateCandidates(
                AgentPathResolver.ExplicitOverride,
                Environment.GetEnvironmentVariable(AgentPathResolver.EnvVar),
                null,
                serviceDir));
            Log($"FATAL: could not find {AgentPathResolver.AgentFileName}. Tried: {tried}. " +
                "Place it next to the service exe, or set RDPEEK_AGENT_EXE / " +
                @"HKLM\SOFTWARE\RDPeek\AgentExe, or pass --agent <path> in the service binPath.");
            // Nothing to supervise — stop cleanly so the SCM doesn't restart-loop us.
            throw new InvalidOperationException("rdpeek-agent.exe not found; see the event log.");
        }

        _supervisor = new AgentSupervisor(agentExe, Log);
        _supervisor.Start();
    }

    protected override void OnStop() => ShutdownSupervisor();
    protected override void OnShutdown() => ShutdownSupervisor();

    private void ShutdownSupervisor()
    {
        try { _supervisor?.Stop(); }
        catch (Exception ex) { Log($"Error during stop: {ex.Message}"); }
        finally { _supervisor?.Dispose(); _supervisor = null; }
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        if (_supervisor is null) return;
        uint sessionId = (uint)change.SessionId;

        switch (change.Reason)
        {
            // A user session became usable — ensure it has an agent.
            case SessionChangeReason.SessionLogon:
            case SessionChangeReason.RemoteConnect:
            case SessionChangeReason.ConsoleConnect:
                Log($"Session {sessionId} {change.Reason}: ensuring agent.");
                _supervisor.OnSessionActive(sessionId);
                break;

            // A session went away — stop tracking and terminate its agent.
            case SessionChangeReason.SessionLogoff:
            case SessionChangeReason.RemoteDisconnect:
            case SessionChangeReason.ConsoleDisconnect:
                Log($"Session {sessionId} {change.Reason}: releasing agent.");
                _supervisor.OnSessionInactive(sessionId);
                break;

            // Lock/unlock/remote-control don't change whether the DVC exists — ignore.
            default:
                break;
        }
    }

    /// <summary>Best-effort logging to the Application event log (service context) + console.</summary>
    internal void Log(string message)
    {
        try { EventLog.WriteEntry(message); } catch { /* event log unavailable */ }
        try { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"); } catch { }
    }
}
