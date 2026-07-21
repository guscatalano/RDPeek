using System.Runtime.InteropServices;
using System.Threading;
using Dvc.Diag.Protocol;
using Google.Protobuf;
using Rdpeek.Client;
using Rdpeek.Protocol;

namespace Rdpeek.Plugin;

/// <summary>
/// Per-connection channel handler. Bridges the agent (over the DVC) to the companion
/// (over the broker pipe): polls the agent for host info + processes and pushes them to
/// the companion for the dashboard. Also reports connection status for the ✓/⚠ view.
/// </summary>
[ComVisible(true)]
internal sealed class ChannelCallback : IWTSVirtualChannelCallback
{
    private readonly IWTSVirtualChannel _channel;
    private readonly FrameDecoder _decoder = new();
    private readonly EnvelopeRouter _router;
    private readonly int _seq;
    private readonly CancellationTokenSource _cts = new();
    private volatile string _host = "";

    public ChannelCallback(IWTSVirtualChannel channel, int seq)
    {
        _channel = channel;
        _seq = seq;
        _router = new EnvelopeRouter(env => { WriteEnvelope(env); return Task.CompletedTask; });

        Broker.Report("connected", Environment.ProcessId, _seq);
        Task.Run(PollLoopAsync);
    }

    private void WriteEnvelope(Envelope env)
    {
        var frame = Frame.Encode(env);
        int hr = _channel.Write((uint)frame.Length, frame, IntPtr.Zero);
        if (hr < 0) Logger.Log($"channel Write failed 0x{hr:X8}");
    }

    /// <summary>Handshake once, then poll host + processes every few seconds and push to the companion.</summary>
    private async Task PollLoopAsync()
    {
        try
        {
            var caps = await RequestAsync(new Envelope
            {
                Hello = new Hello { ProtocolVersion = 1, ClientBuild = "rdpeek-plugin/0.1" },
            });
            if (caps?.BodyCase == Envelope.BodyOneofCase.Capabilities)
                Logger.Log($"agent capabilities: build={caps.Capabilities.AgentBuild} " +
                           $"sysinfo={caps.Capabilities.Sysinfo} processes={caps.Capabilities.ProcessList}");
            else
                Logger.Log($"unexpected reply to Hello: {caps?.BodyCase.ToString() ?? "none"}");

            bool loggedHost = false;
            int cycle = 0;
            while (!_cts.IsCancellationRequested)
            {
                // Every cycle: host info, processes, network, perf (dynamic).
                var snap = await RequestAsync(new Envelope { SysinfoRequest = new SysInfoRequest() });
                if (snap is null) break; // channel dead / timed out
                if (snap.BodyCase == Envelope.BodyOneofCase.SysinfoSnapshot)
                {
                    var s = snap.SysinfoSnapshot;
                    _host = s.HostName;
                    if (!loggedHost)
                    {
                        Logger.Log($"remote host: {s.HostName} — {s.OsProductName} build {s.OsBuild}.{s.OsUbr}");
                        loggedHost = true;
                    }
                    Broker.Report("connected", Environment.ProcessId, _seq, _host);
                    Push("sysinfo", s);
                }

                var procs = await RequestAsync(new Envelope { ProcessListRequest = new ProcessListRequest() });
                if (procs?.BodyCase == Envelope.BodyOneofCase.ProcessList) Push("procs", procs.ProcessList);

                var net = await RequestAsync(new Envelope { NetConnRequest = new NetConnRequest() });
                if (net?.BodyCase == Envelope.BodyOneofCase.NetConnList) Push("net", net.NetConnList);

                var perf = await RequestAsync(new Envelope { PerfRequest = new PerfRequest() });
                if (perf?.BodyCase == Envelope.BodyOneofCase.PerfSnapshot) Push("perf", perf.PerfSnapshot);

                // Every 3rd cycle (~9s): sessions + services (change slowly, bigger payloads).
                if (cycle % 3 == 0)
                {
                    var sess = await RequestAsync(new Envelope { SessionListRequest = new SessionListRequest() });
                    if (sess?.BodyCase == Envelope.BodyOneofCase.SessionList) Push("sessions", sess.SessionList);

                    var svc = await RequestAsync(new Envelope { ServiceListRequest = new ServiceListRequest() });
                    if (svc?.BodyCase == Envelope.BodyOneofCase.ServiceList) Push("services", svc.ServiceList);
                }

                cycle++;
                try { await Task.Delay(3000, _cts.Token); } catch { break; }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"poll loop error: {ex.Message}");
        }
    }

    private void Push(string kind, IMessage message)
        => Broker.Report(kind, Environment.ProcessId, _seq, JsonFormatter.Default.Format(message));

    private async Task<Envelope?> RequestAsync(Envelope request)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(5000);
            return await _router.RequestAsync(request, cts.Token);
        }
        catch
        {
            return null; // timed out, cancelled, or channel error
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
        _cts.Cancel();
        Broker.Report("listening", Environment.ProcessId, _seq);
        return 0;
    }
}
