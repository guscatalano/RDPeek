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
        switch (env.BodyCase)
        {
            case Envelope.BodyOneofCase.Hello:
                _ = _router.RespondAsync(new Envelope { Capabilities = Capabilities() }, env.RequestId);
                break;

            case Envelope.BodyOneofCase.SysinfoRequest:
                // One-shot response; interval subscriptions are added with the transport loop.
                _ = _router.RespondAsync(new Envelope { SysinfoSnapshot = SysInfoCollector.Collect() }, env.RequestId);
                break;

            case Envelope.BodyOneofCase.ProcessListRequest:
                var list = ProcessCollector.Collect(env.ProcessListRequest.AllSessions, _sessionId);
                _ = _router.RespondAsync(new Envelope { ProcessList = list }, env.RequestId);
                break;

            // File transfer, counters, and process actions are wired in later milestones.
            default:
                break;
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
        Counters = false,    // runtime-probed once the OS counters ship
        MaxChunkBytes = 256 * 1024,
    };
}
