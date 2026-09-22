using System.Diagnostics;
using Dvc.Diag.Protocol;
using Rdpeek.Protocol;

namespace Rdpeek.Agent;

/// <summary>
/// Transport-agnostic agent logic: wires an <see cref="EnvelopeRouter"/> to the real
/// collectors. The same core runs behind the DVC channel in production and behind an
/// in-proc loopback in tests — only the router's send delegate differs.
///
/// Capabilities here define the shipped posture. This build is read-only: no process
/// kill, no file push.
/// </summary>
internal sealed class AgentCore
{
    private readonly EnvelopeRouter _router;
    private readonly uint _sessionId = (uint)Process.GetCurrentProcess().SessionId;
    private readonly IReadOnlyList<string> _fileRoots;
    private readonly FilePullService? _filePull;
    private readonly IAgentData _data;
    private readonly bool _fake;
    private readonly bool _allowShell;

    public AgentCore(EnvelopeRouter router, IReadOnlyList<string>? fileRoots = null, IAgentData? data = null, bool allowShell = false)
    {
        _router = router;
        _fake = data is not null and not RealAgentData;
        _data = data ?? new RealAgentData(_sessionId);
        _allowShell = allowShell;
        _fileRoots = fileRoots ?? Array.Empty<string>();
        // File PULL is served from within the advertised roots only (read-only). No roots => the
        // capability is off and every path is rejected.
        if (_fileRoots.Count > 0)
            _filePull = new FilePullService(_router, new DiskFileSource(_fileRoots), chunkSize: 256 * 1024);
        _router.OnMessage += Handle;
    }

    private void Handle(Envelope env)
    {
        // A failure in any collector or channel write must NOT crash the agent — log it,
        // reply with an Error if we can, and keep serving.
        try
        {
            switch (env.BodyCase)
            {
                case Envelope.BodyOneofCase.Hello:
                    _ = _router.RespondAsync(new Envelope { Capabilities = Capabilities() }, env.RequestId);
                    break;

                // Echo verbatim. The client times the round trip on its own clock, so
                // nothing here may depend on the two machines' clocks agreeing.
                case Envelope.BodyOneofCase.Ping:
                    _ = _router.RespondAsync(new Envelope { Ping = new Ping(env.Ping) }, env.RequestId);
                    break;

                case Envelope.BodyOneofCase.SysinfoRequest:
                    _ = _router.RespondAsync(new Envelope { SysinfoSnapshot = _data.SysInfo() }, env.RequestId);
                    break;

                case Envelope.BodyOneofCase.ProcessListRequest:
                    _ = _router.RespondAsync(new Envelope { ProcessList = _data.Processes(env.ProcessListRequest.AllSessions) }, env.RequestId);
                    break;

                case Envelope.BodyOneofCase.NetConnRequest:
                    _ = _router.RespondAsync(new Envelope { NetConnList = _data.NetConn() }, env.RequestId);
                    break;

                case Envelope.BodyOneofCase.SessionListRequest:
                    _ = _router.RespondAsync(new Envelope { SessionList = _data.Sessions() }, env.RequestId);
                    break;

                case Envelope.BodyOneofCase.ServiceListRequest:
                    _ = _router.RespondAsync(new Envelope { ServiceList = _data.Services() }, env.RequestId);
                    break;

                case Envelope.BodyOneofCase.PerfRequest:
                    _ = _router.RespondAsync(new Envelope { PerfSnapshot = _data.Perf() }, env.RequestId);
                    break;

                case Envelope.BodyOneofCase.SystemDetailRequest:
                    _ = _router.RespondAsync(new Envelope { SystemDetail = _data.SystemDetail() }, env.RequestId);
                    break;

                case Envelope.BodyOneofCase.EventLogRequest:
                    _ = _router.RespondAsync(
                        new Envelope { EventLog = _data.EventLog(env.EventLogRequest.LogName, (int)env.EventLogRequest.Max) },
                        env.RequestId);
                    break;

                case Envelope.BodyOneofCase.ShellRequest:
                    // Gated: refuse cleanly unless the agent was explicitly started with --allow-shell.
                    if (!_allowShell)
                        _ = _router.RespondAsync(new Envelope
                        {
                            ShellResult = new ShellResult { Allowed = false, Note = "Shell is disabled. Start the agent with --allow-shell to enable it." },
                        }, env.RequestId);
                    else
                        _ = Task.Run(() =>
                        {
                            var r = ShellExecutor.Run(env.ShellRequest.Command, env.ShellRequest.Shell, (int)env.ShellRequest.TimeoutMs);
                            return _router.RespondAsync(new Envelope { ShellResult = r }, env.RequestId);
                        });
                    break;

                // Periodic pushes aren't wired yet: any interval is answered one-shot,
                // which is what the polling viewer asks for.
                case Envelope.BodyOneofCase.CounterSubscribe:
                    _ = _router.RespondAsync(new Envelope { CounterSample = _data.DvcCounters() }, env.RequestId);
                    break;

                case Envelope.BodyOneofCase.ChannelRosterRequest:
                    _ = _router.RespondAsync(new Envelope { ChannelRoster = DvcCounters.Roster() }, env.RequestId);
                    break;

                // File transfer and process actions are wired in later milestones.
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"handler error for {env.BodyCase}: {ex}");
            try
            {
                _ = _router.RespondAsync(
                    new Envelope { Error = new Error { Code = Error.Types.Code.Unknown, Message = ex.Message } },
                    env.RequestId);
            }
            catch (Exception ex2)
            {
                Logger.Log($"failed to send error response: {ex2.Message}");
            }
        }
    }

    private Capabilities Capabilities()
    {
        var caps = new Capabilities
        {
            ProtocolVersion = 1,
            AgentBuild = _fake ? "rdpeek-agent/0.1 (fake demo)" : "rdpeek-agent/0.1 (read-only)",
            Sysinfo = true,
            ProcessList = true,
            ProcessKill = false,                 // read-only build
            FilePull = _fileRoots.Count > 0,     // pull-only, confined to the roots below
            FilePush = false,
            Counters = DvcCounters.Available,    // perfmon counter set, else the ETW fallback
            SystemDetail = true,                 // build / updates / drivers / PnP (read-only)
            Shell = _allowShell,                 // off unless the agent opted in with --allow-shell
            MaxChunkBytes = 256 * 1024,
        };
        caps.FileRoots.AddRange(_fileRoots);
        return caps;
    }
}
