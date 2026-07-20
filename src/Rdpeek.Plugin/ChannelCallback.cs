using System.Runtime.InteropServices;
using Dvc.Diag.Protocol;
using Rdpeek.Client;
using Rdpeek.Protocol;

namespace Rdpeek.Plugin;

/// <summary>
/// Per-connection channel handler. Receives bytes from the RDP client, feeds the
/// frame decoder + router, and — once connected — proactively queries the agent for
/// capabilities and a host snapshot, logging the result. This is the end-to-end
/// proof: connect a session, and the remote host info appears in the plugin log.
/// </summary>
[ComVisible(true)]
internal sealed class ChannelCallback : IWTSVirtualChannelCallback
{
    private readonly IWTSVirtualChannel _channel;
    private readonly FrameDecoder _decoder = new();
    private readonly EnvelopeRouter _router;
    private readonly int _seq;

    public ChannelCallback(IWTSVirtualChannel channel, int seq)
    {
        _channel = channel;
        _seq = seq;
        _router = new EnvelopeRouter(env => { WriteEnvelope(env); return Task.CompletedTask; });

        // Agent connected on this channel — tell the companion (host filled in below).
        Broker.Report("connected", Environment.ProcessId, _seq);
        Task.Run(BootstrapAsync);
    }

    private void WriteEnvelope(Envelope env)
    {
        var frame = Frame.Encode(env);
        int hr = _channel.Write((uint)frame.Length, frame, IntPtr.Zero);
        if (hr < 0) Logger.Log($"channel Write failed 0x{hr:X8}");
    }

    private async Task BootstrapAsync()
    {
        try
        {
            var caps = await _router.RequestAsync(new Envelope
            {
                Hello = new Hello { ProtocolVersion = 1, ClientBuild = "rdpeek-plugin/0.1" },
            });
            Logger.Log($"agent capabilities: build={caps.Capabilities.AgentBuild} " +
                       $"sysinfo={caps.Capabilities.Sysinfo} processes={caps.Capabilities.ProcessList}");

            var snap = await _router.RequestAsync(new Envelope { SysinfoRequest = new SysInfoRequest() });
            var s = snap.SysinfoSnapshot;
            Logger.Log($"remote host: {s.HostName} — {s.OsProductName} build {s.OsBuild}.{s.OsUbr} " +
                       $"({s.OsDisplayVer}), CPU {s.CpuName} @ {s.CpuPercent}%, up {s.UptimeMs / 1000}s");

            // Re-report with the resolved host so the companion can match this to its window.
            Broker.Report("connected", Environment.ProcessId, _seq, s.HostName);
        }
        catch (Exception ex)
        {
            Logger.Log($"bootstrap error: {ex.Message}");
        }
    }

    public int OnDataReceived(uint cbSize, IntPtr pBuffer)
    {
        try
        {
            var buf = new byte[cbSize];
            Marshal.Copy(pBuffer, buf, 0, (int)cbSize);
            foreach (var env in _decoder.PushEnvelopes(buf))
                _router.Handle(env);
        }
        catch (Exception ex)
        {
            Logger.Log($"OnDataReceived error: {ex.Message}");
        }
        return 0; // S_OK
    }

    public int OnClose()
    {
        Logger.Log("channel closed");
        // Agent gone but the RDP connection may persist — back to awaiting an agent.
        Broker.Report("listening", Environment.ProcessId, _seq);
        return 0;
    }
}
