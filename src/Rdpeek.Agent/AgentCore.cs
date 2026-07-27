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

    public AgentCore(EnvelopeRouter router)
    {
        _router = router;
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

                case Envelope.BodyOneofCase.SysinfoRequest:
                    _ = _router.RespondAsync(new Envelope { SysinfoSnapshot = SysInfoCollector.Collect() }, env.RequestId);
                    break;

                case Envelope.BodyOneofCase.ProcessListRequest:
                    var list = ProcessCollector.Collect(env.ProcessListRequest.AllSessions, _sessionId);
                    _ = _router.RespondAsync(new Envelope { ProcessList = list }, env.RequestId);
                    break;

                case Envelope.BodyOneofCase.NetConnRequest:
                    _ = _router.RespondAsync(new Envelope { NetConnList = NetCollector.Collect() }, env.RequestId);
                    break;

                case Envelope.BodyOneofCase.SessionListRequest:
                    _ = _router.RespondAsync(new Envelope { SessionList = SessionCollector.Collect() }, env.RequestId);
                    break;

                case Envelope.BodyOneofCase.ServiceListRequest:
                    _ = _router.RespondAsync(new Envelope { ServiceList = ServiceCollector.Collect() }, env.RequestId);
                    break;

                case Envelope.BodyOneofCase.PerfRequest:
                    _ = _router.RespondAsync(new Envelope { PerfSnapshot = PerfCollector.Collect() }, env.RequestId);
                    break;

                // Periodic pushes aren't wired yet: any interval is answered one-shot,
                // which is what the polling viewer asks for.
                case Envelope.BodyOneofCase.CounterSubscribe:
                    _ = _router.RespondAsync(new Envelope { CounterSample = DvcCounters.Snapshot() }, env.RequestId);
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

    private static Capabilities Capabilities() => new()
    {
        ProtocolVersion = 1,
        AgentBuild = "rdpeek-agent/0.1 (read-only)",
        Sysinfo = true,
        ProcessList = true,
        ProcessKill = false, // read-only build
        FilePull = false,    // wired in M2
        FilePush = false,
        Counters = DvcCounters.Available,   // perfmon counter set, else the ETW fallback
        MaxChunkBytes = 256 * 1024,
    };
}
