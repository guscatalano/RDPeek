using System.Diagnostics;
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

    // Client-side view of this channel. Bytes are counted where they cross the COM
    // boundary, so they are what mstsc actually moved — independent of anything the
    // agent reports for the same channel.
    private long _bytesSent;
    private long _bytesReceived;
    private long _pings;
    private long _pingTimeouts;
    private double _rttLast, _rttMin = double.MaxValue, _rttSum;

    public ChannelCallback(IWTSVirtualChannel channel, int seq)
    {
        _channel = channel;
        _seq = seq;
        _router = new EnvelopeRouter(env => { WriteEnvelope(env); return Task.CompletedTask; });

        Broker.Report("connected", Environment.ProcessId, _seq);
        Broker.CommandReceived += OnCommand;
        Task.Run(PollLoopAsync);
    }

    private long _nextTid;

    /// <summary>Handle a companion → plugin command. Currently the file pull: pull a remote file over
    /// this channel and write it locally, reporting progress and completion back over the broker.</summary>
    private string _defaultRoot = "";

    private void OnCommand(string line)
    {
        if (Broker.Parse(line) is not { } cmd) return;
        if (cmd.kind == "pull")
        {
            var parts = cmd.payload.Split('\t');
            if (parts.Length >= 2) _ = RunPullAsync(parts[0], parts[1]);
        }
        else if (cmd.kind == "list")
        {
            _ = RunListAsync(cmd.payload);
        }
    }

    /// <summary>List a remote directory (FileListRequest) and report the entries to the companion.
    /// An empty path lists the agent's first advertised file root.</summary>
    private async Task RunListAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) path = _defaultRoot;
        try
        {
            var reply = await _router.RequestAsync(new Envelope { FileListRequest = new FileListRequest { Path = path } }, _cts.Token);
            if (reply.BodyCase == Envelope.BodyOneofCase.FileList)
            {
                var fl = reply.FileList;
                // Single line for the broker: path <RS> name<US>d|f<US>size <RS> ...
                var sb = new System.Text.StringBuilder(fl.Path);
                foreach (var it in fl.Items)
                    sb.Append('\x1e').Append(it.Name).Append('\x1f').Append(it.IsDir ? 'd' : 'f').Append('\x1f').Append(it.Size);
                Broker.Send(Broker.Format("filelist", Environment.ProcessId, _seq, sb.ToString()));
            }
            else
            {
                var msg = reply.BodyCase == Envelope.BodyOneofCase.Error ? reply.Error.Message : reply.BodyCase.ToString();
                Broker.Send(Broker.Format("filelisterror", Environment.ProcessId, _seq, $"{path}\t{msg}"));
            }
        }
        catch (Exception ex)
        {
            Broker.Send(Broker.Format("filelisterror", Environment.ProcessId, _seq, $"{path}\t{ex.Message}"));
        }
    }

    private async Task RunPullAsync(string remotePath, string localDest)
    {
        ulong tid = (ulong)Interlocked.Increment(ref _nextTid);
        Logger.Log($"file pull: '{remotePath}' -> '{localDest}'");
        try
        {
            var dir = Path.GetDirectoryName(localDest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var dest = new FileStream(localDest, FileMode.Create, FileAccess.Write, FileShare.None);

            var receiver = new FilePullReceiver(_router, tid, dest);
            long lastReport = 0;
            receiver.Progress = (written, total) =>
            {
                if (written - lastReport >= 512 * 1024 || written == total)   // throttle
                {
                    lastReport = written;
                    Broker.Send(Broker.Format("pullprogress", Environment.ProcessId, _seq, $"{written}\t{total}\t{localDest}"));
                }
            };

            var result = await receiver.RunAsync(remotePath, _cts.Token);
            Broker.Send(Broker.Format("pulldone", Environment.ProcessId, _seq,
                $"{(result.Ok ? 1 : 0)}\t{result.Bytes}\t{localDest}\t{result.Message}"));
            Logger.Log($"file pull done: ok={result.Ok} bytes={result.Bytes} {result.Message}");
        }
        catch (Exception ex)
        {
            Broker.Send(Broker.Format("pulldone", Environment.ProcessId, _seq, $"0\t0\t{localDest}\t{ex.Message}"));
            Logger.Log($"file pull error: {ex.Message}");
        }
    }

    private readonly FrameInspector _inspector = new();
    private readonly object _tapLock = new();
    private long _framesIn, _framesOut, _frameAnomalies;

    /// <summary>Tap raw channel bytes into the frame inspector (both directions), surfacing per-frame
    /// anomalies to the companion immediately. The inspector isn't thread-safe and the two directions
    /// run on different threads, so serialise.</summary>
    private void Tap(string direction, byte[] bytes)
    {
        try
        {
            IReadOnlyList<FrameRecord> recs;
            lock (_tapLock) recs = _inspector.Push(direction, bytes);
            foreach (var r in recs)
            {
                if (direction == "in") Interlocked.Increment(ref _framesIn);
                else Interlocked.Increment(ref _framesOut);
                if (r.Anomalies.Count > 0)
                {
                    Interlocked.Increment(ref _frameAnomalies);
                    Broker.Send(Broker.Format("frameanomaly", Environment.ProcessId, _seq,
                        $"{r.Direction}\t{r.BodyCase}\t{string.Join("; ", r.Anomalies)}"));
                }
            }
        }
        catch { /* a diagnostic tap must never disturb the channel */ }
    }

    private void WriteEnvelope(Envelope env)
    {
        var frame = Frame.Encode(env);
        int hr = _channel.Write((uint)frame.Length, frame, IntPtr.Zero);
        if (hr < 0) { Logger.Log($"channel Write failed 0x{hr:X8}"); return; }
        Interlocked.Add(ref _bytesSent, frame.Length);
        Tap("out", frame);
    }

    /// <summary>
    /// Round-trip time measured locally: stopwatch before the request, stopwatch after
    /// the echo. Never a difference of the two machines' clocks — they need not agree,
    /// and on a fresh VM they usually don't.
    /// </summary>
    private async Task MeasureRttAsync()
    {
        long started = Stopwatch.GetTimestamp();
        var reply = await RequestAsync(new Envelope
        {
            Ping = new Ping { SequenceNumber = (ulong)Interlocked.Read(ref _pings) + 1 },
        });

        Interlocked.Increment(ref _pings);
        if (reply?.BodyCase != Envelope.BodyOneofCase.Ping)
        {
            Interlocked.Increment(ref _pingTimeouts);
            return;
        }

        double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _rttLast = ms;
        _rttSum += ms;
        if (ms < _rttMin) _rttMin = ms;
    }

    private ClientLink LinkStats()
    {
        long answered = Interlocked.Read(ref _pings) - Interlocked.Read(ref _pingTimeouts);
        return new ClientLink
        {
            Channel = InspectorPlugin.InspectorChannel,
            RttMsLast = _rttLast,
            RttMsMin = _rttMin == double.MaxValue ? 0 : _rttMin,
            RttMsAvg = answered > 0 ? _rttSum / answered : 0,
            BytesSent = (ulong)Interlocked.Read(ref _bytesSent),
            BytesReceived = (ulong)Interlocked.Read(ref _bytesReceived),
            Pings = (ulong)Interlocked.Read(ref _pings),
            PingTimeouts = (ulong)Interlocked.Read(ref _pingTimeouts),
        };
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
            {
                _defaultRoot = caps.Capabilities.FileRoots.FirstOrDefault() ?? "";
                Logger.Log($"agent capabilities: build={caps.Capabilities.AgentBuild} " +
                           $"sysinfo={caps.Capabilities.Sysinfo} processes={caps.Capabilities.ProcessList}");
            }
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

                // interval_ms = 0: one-shot snapshot, not a subscription (see diag.proto).
                var dvc = await RequestAsync(new Envelope { CounterSubscribe = new CounterSubscribe { IntervalMs = 0 } });
                if (dvc?.BodyCase == Envelope.BodyOneofCase.CounterSample) Push("counters", dvc.CounterSample);

                // Last, so the byte counts include everything this cycle sent.
                await MeasureRttAsync();
                Push("link", LinkStats());

                // Frame-inspector stats for the companion's Frames view.
                Broker.Send(Broker.Format("framestats", Environment.ProcessId, _seq,
                    $"{Interlocked.Read(ref _framesIn)}\t{Interlocked.Read(ref _framesOut)}\t{Interlocked.Read(ref _frameAnomalies)}"));

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
            Interlocked.Add(ref _bytesReceived, cbSize);
            Tap("in", buf);
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
        Broker.CommandReceived -= OnCommand;
        _cts.Cancel();
        Broker.Report("listening", Environment.ProcessId, _seq);
        return 0;
    }
}
