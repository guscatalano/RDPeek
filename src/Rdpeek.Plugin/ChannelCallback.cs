using System.Diagnostics;
using System.Globalization;
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
        else if (cmd.kind == "probe")
        {
            int n = int.TryParse(cmd.payload, out var c) ? Math.Clamp(c, 1, 200) : 20;
            _ = RunProbeAsync(n);
        }
    }

    /// <summary>Fire a burst of pings and report the latency distribution — an on-demand channel
    /// health check, separate from the passive RTT the Link tab shows.</summary>
    private async Task RunProbeAsync(int count)
    {
        var samples = new List<double>();
        int lost = 0;
        for (int i = 0; i < count && !_cts.IsCancellationRequested; i++)
        {
            long started = Stopwatch.GetTimestamp();
            var reply = await RequestAsync(new Envelope { Ping = new Ping { SequenceNumber = (ulong)(i + 1) } });
            if (reply?.BodyCase == Envelope.BodyOneofCase.Ping)
                samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            else
                lost++;
            try { await Task.Delay(100, _cts.Token); } catch { break; }
        }

        static string F(double d) => d.ToString("0.0", CultureInfo.InvariantCulture);
        string payload;
        if (samples.Count > 0)
        {
            double min = samples.Min(), max = samples.Max(), avg = samples.Average();
            double jitter = samples.Count > 1
                ? samples.Zip(samples.Skip(1), (a, b) => Math.Abs(b - a)).Average() : 0;
            string list = string.Join(",", samples.Select(F));
            payload = $"{count}\t{samples.Count}\t{lost}\t{F(min)}\t{F(avg)}\t{F(max)}\t{F(jitter)}\t{list}";
        }
        else
        {
            payload = $"{count}\t0\t{lost}\t0\t0\t0\t0\t";
        }
        Broker.Send(Broker.Format("probe", Environment.ProcessId, _seq, payload));
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
                // Single line for the broker: path<US>root <RS> name<US>d|f<US>size<US>mtimeTicks <RS> ...
                var sb = new System.Text.StringBuilder();
                sb.Append(fl.Path).Append('\x1f').Append(_defaultRoot);
                foreach (var it in fl.Items)
                    sb.Append('\x1e').Append(it.Name).Append('\x1f').Append(it.IsDir ? 'd' : 'f')
                      .Append('\x1f').Append(it.Size).Append('\x1f').Append(it.MtimeTicks);
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

    // Rate-limit the per-frame feed to the companion so a bulk file pull can't flood the broker
    // pipe with thousands of rows a second. Token bucket, refilled by elapsed time; anomalous
    // frames bypass it so a problem is never dropped.
    private readonly object _feedLock = new();
    private double _feedTokens = FeedBurst;
    private long _feedTicks = Stopwatch.GetTimestamp();
    private const double FeedRatePerSec = 60;
    private const double FeedBurst = 80;

    private bool FeedAllow()
    {
        lock (_feedLock)
        {
            long now = Stopwatch.GetTimestamp();
            double secs = Stopwatch.GetElapsedTime(_feedTicks, now).TotalSeconds;
            _feedTicks = now;
            _feedTokens = Math.Min(FeedBurst, _feedTokens + secs * FeedRatePerSec);
            if (_feedTokens < 1) return false;
            _feedTokens -= 1;
            return true;
        }
    }

    /// <summary>Tap raw channel bytes into the frame inspector (both directions) and stream each frame
    /// to the companion's live Frames feed — direction, message type, size, request id, and any
    /// anomalies. The inspector isn't thread-safe and the two directions run on different threads,
    /// so serialise.</summary>
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
                bool anomalous = r.Anomalies.Count > 0;
                if (anomalous) Interlocked.Increment(ref _frameAnomalies);

                // frame = direction \t bodyCase \t size \t requestId \t decoded(0/1) \t anomalies \t fields
                // fields = depth \x1f name \x1f value, records joined by \x1e (the decoded field tree,
                // flattened depth-first, for the companion's click-to-decode detail pane).
                if (anomalous || FeedAllow())
                {
                    var fb = new System.Text.StringBuilder();
                    FlattenFields(r.Fields, 0, fb);
                    Broker.Send(Broker.Format("frame", Environment.ProcessId, _seq,
                        $"{r.Direction}\t{r.BodyCase}\t{r.SizeBytes}\t{r.RequestId}\t{(r.Decoded ? 1 : 0)}\t{string.Join("; ", r.Anomalies)}\t{fb}"));
                }
            }
        }
        catch { /* a diagnostic tap must never disturb the channel */ }
    }

    private static void FlattenFields(IReadOnlyList<FrameField> fields, int depth, System.Text.StringBuilder sb)
    {
        foreach (var f in fields)
        {
            if (sb.Length > 0) sb.Append('\x1e');
            sb.Append(depth).Append('\x1f').Append(Clean(f.Name)).Append('\x1f').Append(Clean(f.Value));
            if (f.Children.Count > 0) FlattenFields(f.Children, depth + 1, sb);
        }
    }

    // The broker line is newline-terminated and the feed uses \t/\x1e/\x1f as separators, so a field
    // name or value must not contain any of them.
    private static string Clean(string s) =>
        s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Replace('\x1e', ' ').Replace('\x1f', ' ');

    private readonly object _writeLock = new();

    private void WriteEnvelope(Envelope env)
    {
        var frame = Frame.Encode(env);
        int hr;
        // The fast and slow poll loops both issue requests, so two threads can reach the channel
        // at once — serialise the native Write (concurrency correlates fine by request_id).
        lock (_writeLock) hr = _channel.Write((uint)frame.Length, frame, IntPtr.Zero);
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

            // Live signals (RTT, DVC counters, frame stats) tick fast; inventory is polled slower.
            _ = FastLoopAsync();

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

    /// <summary>The "live" signals a user watches change in real time — round-trip time, per-channel
    /// DVC traffic, and frame counts — polled at ~1s instead of riding the 3s inventory cycle.</summary>
    private async Task FastLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await MeasureRttAsync();
                Push("link", LinkStats());

                // interval_ms = 0: one-shot snapshot, not a subscription (see diag.proto).
                var dvc = await RequestAsync(new Envelope { CounterSubscribe = new CounterSubscribe { IntervalMs = 0 } });
                if (dvc?.BodyCase == Envelope.BodyOneofCase.CounterSample) Push("counters", dvc.CounterSample);

                Broker.Send(Broker.Format("framestats", Environment.ProcessId, _seq,
                    $"{Interlocked.Read(ref _framesIn)}\t{Interlocked.Read(ref _framesOut)}\t{Interlocked.Read(ref _frameAnomalies)}"));

                try { await Task.Delay(1000, _cts.Token); } catch { break; }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"fast loop error: {ex.Message}");
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
