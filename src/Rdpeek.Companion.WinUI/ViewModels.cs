using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Principal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dvc.Diag.Protocol;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Rdpeek.Client;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Rdpeek.Companion.WinUI;

/// <summary>One RDP connection (an mstsc window) + its correlated agent state.</summary>
public partial class ConnectionRow : ObservableObject
{
    public IntPtr Hwnd { get; init; }
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _agent = "—";
    [ObservableProperty] private string _window = "";
    // Live at-a-glance metrics + health, so every connection is legible without selecting it.
    [ObservableProperty] private string _cpu = "";
    [ObservableProperty] private string _rtt = "";
    [ObservableProperty] private string _throughput = "";
    [ObservableProperty] private string _healthText = "";
    [ObservableProperty] private Brush _healthBrush = UiBrushes.Muted;
    public BrokerServer.AgentState? State { get; set; }
}

public sealed record ProcRow(uint Pid, string Image, string User, string Mem);

public sealed record ChannelRow(string Name, string Kind, string Activation, string Module, string Clsid);

/// <summary>Cached brushes for state coloring, so records don't allocate one per row.</summary>
internal static class UiBrushes
{
    public static readonly Brush Ok = new SolidColorBrush(Colors.MediumSeaGreen);
    public static readonly Brush Info = new SolidColorBrush(Colors.CornflowerBlue);
    public static readonly Brush Muted = new SolidColorBrush(Colors.Gray);
    public static readonly Brush Warn = new SolidColorBrush(Colors.Goldenrod);
    public static readonly Brush Fail = new SolidColorBrush(Colors.IndianRed);
}

/// <summary>x:Bind function helpers for severity → colour/glyph in the Diagnostics tab.</summary>
public static class Ui
{
    public static Brush SeverityBrush(string severity) => severity switch
    {
        "Pass" => UiBrushes.Ok,
        "Warn" => UiBrushes.Warn,
        "Fail" => UiBrushes.Fail,
        _ => UiBrushes.Muted,
    };

    public static string SeverityGlyph(string severity) => severity switch
    {
        "Pass" => "",   // check
        "Warn" => "",   // warning
        "Fail" => "",   // cancel
        _ => "",        // info
    };

    public static string PluginDetail(string activation, string module, string bitness)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(activation)) parts.Add(activation);
        if (!string.IsNullOrEmpty(module)) parts.Add(module + (string.IsNullOrEmpty(bitness) ? "" : $" ({bitness})"));
        return string.Join("    ·    ", parts);
    }
}

public sealed record NetRow(string Proto, string Local, string Remote, string State, string Process)
{
    public Brush StateBrush => State switch
    {
        "ESTABLISHED" => UiBrushes.Ok,
        "LISTEN" => UiBrushes.Info,
        _ => UiBrushes.Muted,
    };
}

public sealed record SessionRow(uint Id, string Station, string User, string State, string Client)
{
    public Brush StateBrush => State is "Active" or "Connected" ? UiBrushes.Ok : UiBrushes.Muted;
}

public sealed record ServiceRow(string Name, string Status, string StartType, string Display)
{
    public Brush StatusBrush => Status == "Running" ? UiBrushes.Ok : UiBrushes.Muted;
}

/// <summary>Live traffic on one remote DVC, as measured on the session host.</summary>
public sealed record DvcRow(string Name, string Sent, string Received, string SendRate, string RecvRate, string Rtt);

/// <summary>One RemoteFX link/graphics counter, as Windows names it.</summary>
public sealed record LinkRow(string Name, string Value, string Instance);

/// <summary>An installed Windows update (QFE).</summary>
public sealed record HotfixRow(string Id, string Description, string Installed);

/// <summary>A display adapter and its driver.</summary>
public sealed record GpuRow(string Name, string Driver, string Date, string Vram, string Status);

/// <summary>A PnP device; HasProblem drives the red highlight.</summary>
public sealed record DeviceRow(string Name, string Class, string Status, string Problem, bool HasProblem)
{
    public Brush StatusBrush => HasProblem ? UiBrushes.Fail : UiBrushes.Muted;
}

/// <summary>One Windows Event Log entry.</summary>
public sealed record EventRow(string Level, string Time, string Source, string Id, string Message)
{
    public Brush LevelBrush => Level switch
    {
        "Error" or "Critical" => UiBrushes.Fail,
        "Warning" => UiBrushes.Warn,
        _ => UiBrushes.Muted,
    };
}

/// <summary>One decoded field of a frame, flattened for the detail pane. Indent is the tree depth
/// rendered as a left margin.</summary>
public sealed record FrameFieldRow(string Text, double Indent)
{
    public Thickness IndentThickness => new(Indent, 1, 0, 1);
}

/// <summary>One frame the inspector tapped on the channel, for the live Frames feed. Note carries
/// any anomaly text; IsAnomaly flags the row for highlighting; Fields is the decoded field tree.</summary>
public sealed record FrameRow(long Seq, string Time, string Dir, string Message, string Size, string Req,
    string Note, bool IsAnomaly, IReadOnlyList<FrameFieldRow> Fields);

/// <summary>An entry in the remote file browser.</summary>
public sealed record RemoteEntry(string Glyph, string Name, string Size, string Modified, bool IsDir, string FullPath);

/// <summary>One clickable segment of the file browser's path.</summary>
public sealed record BreadcrumbRow(string Name, string FullPath);

/// <summary>One bar in the latency-probe mini chart (height in px).</summary>
public sealed record ProbeBar(double Height);

public partial class MainViewModel : ObservableObject
{
    private const string InstallCommand =
        "irm https://raw.githubusercontent.com/guscatalano/RDPeek/main/tools/install-agent-web.ps1 | iex";

    private readonly BrokerServer _broker = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;

    public ObservableCollection<ConnectionRow> Connections { get; } = new();
    public ObservableCollection<ProcRow> Processes { get; } = new();
    public ObservableCollection<ChannelRow> Channels { get; } = new();
    public ObservableCollection<NetRow> Network { get; } = new();
    public ObservableCollection<SessionRow> Sessions { get; } = new();
    public ObservableCollection<ServiceRow> Services { get; } = new();
    public ObservableCollection<DvcRow> DvcTraffic { get; } = new();
    public ObservableCollection<LinkRow> LinkQuality { get; } = new();
    public ObservableCollection<HotfixRow> Hotfixes { get; } = new();
    public ObservableCollection<GpuRow> Gpus { get; } = new();
    public ObservableCollection<DeviceRow> Devices { get; } = new();
    [ObservableProperty] private string _systemBuild = "Connect an agent to read deep system detail.";

    // Persistent connection/status bar (shown above every tab).
    private DateTime? _lastAgentUpdate;
    private static readonly Brush LiveBrush = new SolidColorBrush(Colors.LimeGreen);
    private static readonly Brush StaleBrush = new SolidColorBrush(Colors.Gray);
    [ObservableProperty] private string _connectionSummary = "No agent connected.";
    [ObservableProperty] private string _freshness = "";
    [ObservableProperty] private Brush _livenessBrush = StaleBrush;

    // Sparklines: short history of the fast-moving metrics, pre-scaled to a fixed pixel box.
    private const int SparkCap = 90;                 // ~90 s at the 1 s sample cadence
    private const double SparkW = 130, SparkH = 30;
    private readonly Queue<double> _cpuHist = new(), _rttHist = new(), _tputHist = new();
    [ObservableProperty] private PointCollection _cpuSpark = new();
    [ObservableProperty] private PointCollection _rttSpark = new();
    [ObservableProperty] private PointCollection _tputSpark = new();
    [ObservableProperty] private string _cpuSparkLabel = "—";
    [ObservableProperty] private string _rttSparkLabel = "—";
    [ObservableProperty] private string _tputSparkLabel = "—";

    [ObservableProperty] private ConnectionRow? _selectedConnection;
    [ObservableProperty] private string _hostHeader = "Connect an RDP session to see host details.";
    [ObservableProperty] private string _perfText = "";
    [ObservableProperty] private string _dvcNote = "Waiting for the agent to report channel traffic…";
    [ObservableProperty] private string _clientMeasured = "";

    // Channel drill-down: select a row in the DVC traffic table to see its full detail.
    private string _selectedChannelName = "";
    [ObservableProperty] private DvcRow? _selectedDvcChannel;
    [ObservableProperty] private string _channelDetail = "Select a channel above to see its config and full counters.";

    // Server-vs-client round-trip on the diagnostics channel (the DESIGN.md asymmetry, made legible).
    [ObservableProperty] private string _rttServer = "—";
    [ObservableProperty] private string _rttClient = "—";
    [ObservableProperty] private string _rttGap = "—";
    [ObservableProperty] private string _rttExplain =
        "Waiting for both the agent's and the plugin's measurements of the diagnostics channel.";
    [ObservableProperty] private string _linkNote = "";
    [ObservableProperty] private string _status = "Starting…";

    // File pull (dashboard → plugin → agent → local disk).
    [ObservableProperty] private string _remotePath = "";
    [ObservableProperty] private string _localDest = "";
    [ObservableProperty] private double _pullProgress;      // 0..100
    [ObservableProperty] private string _pullStatus = "Pull a file from the remote session onto this machine.";

    // Frame inspector — live feed with filtering and a click-to-decode detail pane.
    private const string AllTypes = "(all types)";
    private long _frameSeq;
    private readonly List<FrameRow> _allFrames = new();     // backing store (newest first, capped)
    [ObservableProperty] private string _frameStats = "No frames tapped yet — connect an agent.";
    [ObservableProperty] private string _frameFilterMode = "All";     // All / In / Out / Anomalies
    [ObservableProperty] private string _frameTypeFilter = AllTypes;
    [ObservableProperty] private FrameRow? _selectedFrame;
    [ObservableProperty] private string _frameDetailHeader = "Select a frame to see its decoded fields.";
    public ObservableCollection<FrameRow> FrameFeed { get; } = new();          // filtered, visible
    public ObservableCollection<string> FrameTypes { get; } = new() { AllTypes };
    public ObservableCollection<FrameFieldRow> SelectedFrameFields { get; } = new();

    // Remote file browser.
    [ObservableProperty] private string _currentRemotePath = "";
    [ObservableProperty] private bool _browserLoading;
    public ObservableCollection<RemoteEntry> RemoteEntries { get; } = new();
    public ObservableCollection<BreadcrumbRow> Breadcrumbs { get; } = new();

    // Diagnostics (Doctor in the UI) — plugin registration health.
    [ObservableProperty] private string _diagnosticsSummary = "Checking plugin registration…";
    public ObservableCollection<PluginReport> Diagnostics { get; } = new();

    // Windows Event Log viewer.
    public ObservableCollection<EventRow> EventLogEntries { get; } = new();
    public ObservableCollection<string> EventLogNames { get; } = new() { "System", "Application", "Setup" };
    [ObservableProperty] private string _selectedEventLog = "System";
    [ObservableProperty] private bool _eventLogLoading;
    [ObservableProperty] private string _eventLogNote = "Pick a log and Refresh to read the remote Event Log.";

    // Latency probe (on-demand ping burst on the diagnostics channel).
    [ObservableProperty] private string _probeResult = "Run a burst of pings to measure this channel's latency.";
    [ObservableProperty] private bool _probeRunning;
    public ObservableCollection<ProbeBar> ProbeSamples { get; } = new();

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        // Just stamp freshness on each push; the 1s timer does the (heavy) Refresh. With several
        // connections each pushing a few times a second, refreshing per-push churned the UI.
        _broker.Changed += () => _dispatcher.TryEnqueue(() => _lastAgentUpdate = DateTime.UtcNow);
        _broker.PullUpdate += (kind, payload) => _dispatcher.TryEnqueue(() => OnPullUpdate(kind, payload));
        _broker.FrameUpdate += (pid, seq, kind, payload) => _dispatcher.TryEnqueue(() => OnFrameUpdate(pid, seq, kind, payload));
        _broker.FileListUpdate += (kind, payload) => _dispatcher.TryEnqueue(() => OnFileList(kind, payload));
        _broker.ProbeUpdate += payload => _dispatcher.TryEnqueue(() => OnProbe(payload));
        _broker.EventLogUpdate += payload => _dispatcher.TryEnqueue(() => OnEventLog(payload));
        _broker.Start();

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => { Refresh(); SampleSparklines(); };
        _timer.Start();

        Refresh();
        _ = RunDiagnostics();   // plugin-registration health, in the background
    }

    partial void OnSelectedConnectionChanged(ConnectionRow? value)
    {
        UpdateDetails();
        // Frames, the probe and the file browser are per-connection and can't be replayed, so reset
        // them for the newly-selected host instead of showing the previous one's data.
        _allFrames.Clear();
        FrameFeed.Clear();
        FrameTypes.Clear();
        FrameTypes.Add(AllTypes);
        SelectedFrame = null;
        FrameStats = "No frames tapped yet for this connection.";
        ProbeSamples.Clear();
        ProbeResult = "Run a burst of pings to measure this channel's latency.";
        RemoteEntries.Clear();
        Breadcrumbs.Clear();
        CurrentRemotePath = "";
        EventLogEntries.Clear();
        EventLogNote = "Pick a log and Refresh to read the remote Event Log.";
    }

    partial void OnRemotePathChanged(string value)
    {
        // Auto-fill a sensible local destination (Downloads\<leaf>) when the user hasn't set one.
        if (string.IsNullOrWhiteSpace(LocalDest) && !string.IsNullOrWhiteSpace(value))
        {
            var leaf = value.Replace('/', '\\').TrimEnd('\\');
            leaf = leaf[(leaf.LastIndexOf('\\') + 1)..];
            if (leaf.Length > 0)
                LocalDest = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", leaf);
        }
    }

    [RelayCommand]
    private void Pull()
    {
        var st = SelectedConnection?.State ?? Connections.Select(c => c.State).FirstOrDefault(s => s?.Status == "connected");
        if (st is null || st.Status != "connected") { PullStatus = "No connected agent to pull from."; return; }
        if (string.IsNullOrWhiteSpace(RemotePath) || string.IsNullOrWhiteSpace(LocalDest))
        { PullStatus = "Enter a remote path and a local destination."; return; }

        if (_broker.SendCommand(st.Pid, Broker.Format("pull", st.Pid, st.Seq, $"{RemotePath}\t{LocalDest}")))
        {
            PullProgress = 0;
            PullStatus = $"Pulling {RemotePath} …";
        }
        else
        {
            PullStatus = "Couldn't reach the plugin (is the agent connected?).";
        }
    }

    private void OnPullUpdate(string kind, string payload)
    {
        var p = payload.Split('\t');
        if (kind == "pullprogress" && p.Length >= 2 &&
            long.TryParse(p[0], out var written) && long.TryParse(p[1], out var total))
        {
            PullProgress = total > 0 ? Math.Min(100, written * 100.0 / total) : 0;
            PullStatus = $"Pulling… {Bytes((ulong)written)}{(total > 0 ? " / " + Bytes((ulong)total) : "")}";
        }
        else if (kind == "pulldone" && p.Length >= 3)
        {
            bool ok = p[0] == "1";
            PullProgress = ok ? 100 : PullProgress;
            PullStatus = ok
                ? $"Saved {Bytes(ulong.TryParse(p[1], out var b) ? b : 0)} to {p[2]}"
                : $"Failed: {(p.Length > 3 ? p[3] : "unknown error")}";
        }
    }

    private void SampleSparklines()
    {
        var st = ConnectedAgent();
        if (st?.Status != "connected") return;

        double cpu = st.Sysinfo?.CpuPercent ?? 0;
        double rtt = st.Link?.RttMsLast ?? 0;
        double tput = st.Counters?.Channels.Sum(c => c.SendRateBps + c.RecvRateBps) ?? 0;

        Roll(_cpuHist, cpu);
        Roll(_rttHist, rtt);
        Roll(_tputHist, tput);

        CpuSpark = BuildSpark(_cpuHist, fixedMax: 100);   // percent
        RttSpark = BuildSpark(_rttHist);
        TputSpark = BuildSpark(_tputHist);

        CpuSparkLabel = st.Sysinfo is null ? "—" : $"{cpu:0.0} %";
        RttSparkLabel = st.Link is null ? "—" : $"{rtt:0.0} ms";
        TputSparkLabel = st.Counters is null ? "—" : Rate(tput);
    }

    private static void Roll(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > SparkCap) q.Dequeue();
    }

    /// <summary>Scale a history buffer to a Polyline over a fixed WxH box, baseline at 0.</summary>
    private static PointCollection BuildSpark(IReadOnlyCollection<double> data, double? fixedMax = null)
    {
        var pts = new PointCollection();
        if (data.Count < 2) return pts;
        var arr = data.ToArray();
        double max = Math.Max(fixedMax ?? arr.Max(), 0.0001);
        double stepX = SparkW / (arr.Length - 1);
        for (int i = 0; i < arr.Length; i++)
        {
            double y = SparkH - Math.Clamp(arr[i] / max, 0, 1) * SparkH;
            pts.Add(new Point(i * stepX, y));
        }
        return pts;
    }

    private BrokerServer.AgentState? ConnectedAgent() =>
        SelectedConnection?.State ?? Connections.Select(c => c.State).FirstOrDefault(s => s?.Status == "connected");

    [RelayCommand]
    private void Browse()
    {
        var st = ConnectedAgent();
        if (st is null) { PullStatus = "No connected agent to browse."; return; }
        BrowserLoading = true;
        SendList(st, "");   // empty path → the agent's first file root
    }

    [RelayCommand]
    private void OpenEntry(RemoteEntry? entry)
    {
        if (entry is null) return;
        var st = ConnectedAgent();
        if (st is null) return;
        if (entry.IsDir) { BrowserLoading = true; SendList(st, entry.FullPath); }
        else { RemotePath = entry.FullPath; OnRemotePathChanged(entry.FullPath); PullStatus = $"Selected {entry.Name} — double-click or click Pull to fetch it."; }
    }

    /// <summary>Navigate the browser to a directory (used by the breadcrumb).</summary>
    [RelayCommand]
    private void Navigate(string? path)
    {
        if (path is null) return;
        var st = ConnectedAgent();
        if (st is null) return;
        BrowserLoading = true;
        SendList(st, path);
    }

    /// <summary>Re-list the current directory (or the root if none is open yet).</summary>
    [RelayCommand]
    private void RefreshDir()
    {
        if (string.IsNullOrEmpty(CurrentRemotePath)) Browse();
        else Navigate(CurrentRemotePath);
    }

    /// <summary>Double-click a file → select and pull it in one gesture (called from code-behind).</summary>
    public void PullEntry(RemoteEntry entry)
    {
        if (entry.IsDir) { OpenEntry(entry); return; }
        RemotePath = entry.FullPath;
        OnRemotePathChanged(entry.FullPath);
        Pull();
    }

    private void SendList(BrokerServer.AgentState st, string path)
    {
        if (!_broker.SendCommand(st.Pid, Broker.Format("list", st.Pid, st.Seq, path)))
        { PullStatus = "Couldn't reach the plugin."; BrowserLoading = false; }
    }

    private void OnFileList(string kind, string payload)
    {
        BrowserLoading = false;
        if (kind == "filelisterror")
        {
            var e = payload.Split('\t');
            PullStatus = $"Couldn't list {(e.Length > 0 ? e[0] : "")}: {(e.Length > 1 ? e[1] : "error")}";
            return;
        }

        var recs = payload.Split('\x1e');
        var head = recs[0].Split('\x1f');          // path <US> root
        string path = head[0];
        string root = head.Length > 1 ? head[1] : head[0];
        CurrentRemotePath = path;
        BuildBreadcrumbs(root, path);

        var entries = new List<RemoteEntry>();
        foreach (var r in recs.Skip(1))
        {
            var f = r.Split('\x1f');
            if (f.Length < 3) continue;
            bool dir = f[1] == "d";
            string size = dir ? "" : (ulong.TryParse(f[2], out var b) ? Bytes(b) : "");
            string modified = f.Length > 3 && long.TryParse(f[3], out var ticks) && ticks > 0
                ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "";
            entries.Add(new RemoteEntry(dir ? "" : "", f[0], size, modified, dir, CombinePath(path, f[0])));
        }

        RemoteEntries.Clear();
        foreach (var e in entries.OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            RemoteEntries.Add(e);
        PullStatus = RemoteEntries.Count == 0 ? "(empty directory)" : path;
    }

    private void BuildBreadcrumbs(string root, string path)
    {
        Breadcrumbs.Clear();
        root = root.TrimEnd('\\');
        path = path.TrimEnd('\\');
        if (root.Length == 0) { Breadcrumbs.Add(new BreadcrumbRow(path, path)); return; }

        // Root crumb labelled by its leaf; then one crumb per subfolder below the root.
        int slash = root.LastIndexOf('\\');
        Breadcrumbs.Add(new BreadcrumbRow(slash >= 0 ? root[(slash + 1)..] : root, root));
        if (path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            string acc = root;
            foreach (var seg in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                acc = acc + "\\" + seg;
                Breadcrumbs.Add(new BreadcrumbRow(seg, acc));
            }
        }
    }

    private static string CombinePath(string dir, string name) => dir.TrimEnd('\\') + "\\" + name;

    private void OnFrameUpdate(int pid, int seq, string kind, string payload)
    {
        // Frames stream from every connection; show only the one being viewed.
        var sel = SelectedConnection?.State;
        if (sel is null || sel.Pid != pid || sel.Seq != seq) return;

        var p = payload.Split('\t');
        if (kind == "framestats" && p.Length >= 3)
        {
            FrameStats = $"{p[0]} frames in · {p[1]} out · {p[2]} anomalies";
        }
        else if (kind == "frame" && p.Length >= 6)
        {
            // direction \t bodyCase \t size \t requestId \t decoded(0/1) \t anomalies \t fields
            bool decoded = p[4] == "1";
            bool anom = p[5].Length > 0;
            string dir = p[0] == "in" ? "↓ in" : "↑ out";
            string size = long.TryParse(p[2], out var sz) ? Bytes((ulong)sz) : p[2];
            string req = p[3] == "0" ? "" : p[3];
            string note = anom ? "⚠ " + p[5] : (decoded ? "" : "undecodable");
            var fields = ParseFields(p.Length >= 7 ? p[6] : "");
            AddFrame(new FrameRow(++_frameSeq, DateTime.Now.ToString("HH:mm:ss.fff"), dir,
                p[1], size, req, note, anom || !decoded, fields));
        }
    }

    private static IReadOnlyList<FrameFieldRow> ParseFields(string blob)
    {
        if (blob.Length == 0) return Array.Empty<FrameFieldRow>();
        var rows = new List<FrameFieldRow>();
        foreach (var rec in blob.Split('\x1e'))
        {
            var f = rec.Split('\x1f');
            if (f.Length < 3) continue;
            int depth = int.TryParse(f[0], out var d) ? d : 0;
            string text = f[2].Length > 0 ? $"{f[1]}: {f[2]}" : f[1];
            rows.Add(new FrameFieldRow(text, 4 + depth * 16));
        }
        return rows;
    }

    private void AddFrame(FrameRow row)
    {
        _allFrames.Insert(0, row);
        if (!FrameTypes.Contains(row.Message))
        {
            // Keep "(all types)" first, the rest sorted.
            int i = 1;
            while (i < FrameTypes.Count && string.CompareOrdinal(FrameTypes[i], row.Message) < 0) i++;
            FrameTypes.Insert(i, row.Message);
        }
        if (FramePasses(row)) FrameFeed.Insert(0, row);

        while (_allFrames.Count > 300)
        {
            var old = _allFrames[^1];
            _allFrames.RemoveAt(_allFrames.Count - 1);
            FrameFeed.Remove(old);   // FrameRow.Seq makes each row value-unique
        }
    }

    private bool FramePasses(FrameRow r) =>
        (FrameFilterMode switch
        {
            "In" => r.Dir.Contains("in"),
            "Out" => r.Dir.Contains("out"),
            "Anomalies" => r.IsAnomaly,
            _ => true,
        })
        && (FrameTypeFilter == AllTypes || r.Message == FrameTypeFilter);

    partial void OnFrameFilterModeChanged(string value) => RebuildFrameFeed();
    partial void OnFrameTypeFilterChanged(string value) => RebuildFrameFeed();

    private void RebuildFrameFeed()
    {
        FrameFeed.Clear();
        foreach (var r in _allFrames)   // already newest-first
            if (FramePasses(r)) FrameFeed.Add(r);
    }

    partial void OnSelectedFrameChanged(FrameRow? value)
    {
        SelectedFrameFields.Clear();
        if (value is null)
        {
            FrameDetailHeader = "Select a frame to see its decoded fields.";
            return;
        }
        FrameDetailHeader = $"{value.Dir}   ·   {value.Message}   ·   {value.Size}" +
            (string.IsNullOrEmpty(value.Req) ? "" : $"   ·   req {value.Req}") +
            (value.Fields.Count == 0 ? "   —   no decoded body" : "");
        foreach (var f in value.Fields) SelectedFrameFields.Add(f);
    }

    [RelayCommand]
    private void Probe()
    {
        var st = ConnectedAgent();
        if (st is null) { ProbeResult = "No connected agent to probe."; return; }
        ProbeSamples.Clear();
        ProbeResult = "Probing…";
        ProbeRunning = true;
        if (!_broker.SendCommand(st.Pid, Broker.Format("probe", st.Pid, st.Seq, "20")))
        { ProbeResult = "Couldn't reach the plugin."; ProbeRunning = false; }
    }

    private void OnProbe(string payload)
    {
        ProbeRunning = false;
        var p = payload.Split('\t');
        if (p.Length < 7) { ProbeResult = "Probe returned no data."; return; }

        // count sent \t received \t lost \t min \t avg \t max \t jitter \t samples
        ProbeResult = $"sent {p[0]}  ·  received {p[1]}  ·  lost {p[2]}  ·  " +
                      $"min {p[3]}  ·  avg {p[4]}  ·  max {p[5]}  ·  jitter {p[6]} ms";

        ProbeSamples.Clear();
        if (p.Length >= 8 && p[7].Length > 0)
        {
            var vals = p[7].Split(',')
                .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0)
                .ToList();
            double max = Math.Max(vals.DefaultIfEmpty(0).Max(), 0.001);
            foreach (var v in vals) ProbeSamples.Add(new ProbeBar(Math.Max(2, v / max * 40)));
        }
    }

    [RelayCommand]
    private void FetchEventLog()
    {
        var st = ConnectedAgent();
        if (st is null) { EventLogNote = "No connected agent to read the Event Log from."; return; }
        EventLogLoading = true;
        EventLogNote = $"Reading {SelectedEventLog}…";
        if (!_broker.SendCommand(st.Pid, Broker.Format("eventlog", st.Pid, st.Seq, $"{SelectedEventLog}\t100")))
        { EventLogNote = "Couldn't reach the plugin."; EventLogLoading = false; }
    }

    partial void OnSelectedEventLogChanged(string value) => FetchEventLog();

    private void OnEventLog(string payload)
    {
        EventLogLoading = false;
        var recs = payload.Split('\x1e');
        var head = recs[0].Split('\x1f');
        string logName = head[0];
        string note = head.Length > 1 ? head[1] : "";

        EventLogEntries.Clear();
        foreach (var r in recs.Skip(1))
        {
            var f = r.Split('\x1f');
            if (f.Length < 5) continue;
            EventLogEntries.Add(new EventRow(f[0], f[1], f[2], f[3], f[4]));
        }

        EventLogNote = EventLogEntries.Count > 0
            ? $"{logName} — {EventLogEntries.Count} most recent entries"
            : note.Length > 0 ? $"{logName}: {note}" : $"{logName}: no entries";
    }

    [RelayCommand]
    private async Task RunDiagnostics()
    {
        DiagnosticsSummary = "Running registration checks…";
        IReadOnlyList<PluginReport> reports;
        try { reports = await Task.Run(PluginDoctor.Run); }
        catch (Exception ex) { DiagnosticsSummary = $"Diagnostics failed: {ex.Message}"; return; }

        Diagnostics.Clear();
        foreach (var r in reports) Diagnostics.Add(r);

        int fail = reports.Count(r => r.Worst == "Fail");
        int warn = reports.Count(r => r.Worst == "Warn");
        DiagnosticsSummary = reports.Count == 0
            ? "No DVC plugins registered under Terminal Server Client\\AddIns (nothing to diagnose)."
            : $"{reports.Count} plugin(s): {reports.Count - fail - warn} ok · {warn} warning(s) · {fail} failure(s).";
    }

    [RelayCommand]
    private void ExportBundle()
    {
        try
        {
            var st = ConnectedAgent();
            var s = st?.Sysinfo;
            var bundle = new
            {
                generatedUtc = DateTime.UtcNow,
                connection = ConnectionSummary,
                host = s is null ? null : new
                {
                    s.HostName, s.OsProductName, s.OsDisplayVer, build = $"{s.OsBuild}.{s.OsUbr}",
                    s.CpuName, s.CpuLogical, s.CpuPercent, s.MemTotalBytes, s.MemAvailBytes,
                    s.UserName, s.SessionId, s.ClientName, s.Protocol, s.UptimeMs,
                },
                channels = Channels.Select(c => new { c.Name, c.Kind, c.Activation, c.Module, c.Clsid }),
                dvcTraffic = DvcTraffic.Select(d => new { d.Name, d.Sent, d.Received, d.SendRate, d.RecvRate, d.Rtt }),
                linkQuality = LinkQuality.Select(l => new { l.Name, l.Value, l.Instance }),
                rtt = new { server = RttServer, client = RttClient, gap = RttGap, note = RttExplain },
                clientMeasured = ClientMeasured,
                systemDetail = st?.System is not { } sd ? null : new
                {
                    sd.BuildLabEx, sd.BuildLab, sd.EditionId, sd.DisplayVersion, sd.InstallDate,
                    sd.RegisteredOwner, sd.UptimeMs,
                    hotfixes = sd.Hotfixes.Select(h => new { h.HotfixId, h.Description, h.InstalledOn }),
                    gpus = sd.Gpus.Select(g => new { g.Name, g.DriverVersion, g.DriverDate, g.VramBytes, g.Status }),
                    devices = sd.Devices.Select(p => new { p.Name, p.DeviceClass, p.Status, p.Problem }),
                    notes = sd.Notes,
                },
                diagnostics = Diagnostics.Select(r => new
                {
                    r.PluginKey, r.Source, r.Name, r.Activation, r.ModulePath, r.Bitness, r.Clsid, r.Worst,
                    checks = r.Checks.Select(c => new { c.Severity, c.Message }),
                }),
                recentFrames = _allFrames.Select(f => new
                {
                    f.Time, f.Dir, f.Message, f.Size, f.Req, f.Note, fields = f.Fields.Select(x => x.Text),
                }),
            };

            string json = System.Text.Json.JsonSerializer.Serialize(
                bundle, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            string host = s?.HostName is { Length: > 0 } h ? h : "session";
            string safe = new string(host.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
            string file = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads",
                $"rdpeek-bundle-{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            System.IO.File.WriteAllText(file, json);
            Status = $"Support bundle written to {file}";
        }
        catch (Exception ex)
        {
            Status = $"Bundle export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CopyInstall()
    {
        var pkg = new DataPackage();
        pkg.SetText(InstallCommand);
        Clipboard.SetContent(pkg);
        Status = "Install command copied — paste it into a PowerShell in the remote session (one-time).";
    }

    [RelayCommand]
    private void Refresh()
    {
        var windows = RdpWindows.Enumerate();
        var states = _broker.Snapshot();

        // Update/add a row per open RDP window (in place, to keep selection stable).
        var seen = new HashSet<IntPtr>();
        foreach (var w in windows)
        {
            seen.Add(w.Hwnd);
            var row = Connections.FirstOrDefault(c => c.Hwnd == w.Hwnd);
            if (row is null) { row = new ConnectionRow { Hwnd = w.Hwnd }; Connections.Add(row); }
            row.Host = w.Host;
            row.Window = w.Title;
            row.State = Correlate(w, windows.Count, states, out string agentText);
            row.Agent = agentText;
            UpdateRowMetrics(row);
        }
        for (int i = Connections.Count - 1; i >= 0; i--)
            if (!seen.Contains(Connections[i].Hwnd)) Connections.RemoveAt(i);

        if (SelectedConnection is null && Connections.Count > 0)
            SelectedConnection = Connections.FirstOrDefault(c => c.State?.Status == "connected") ?? Connections[0];

        UpdateDetails();
        UpdateChannels();
        UpdateStatus(windows.Count, states);
        UpdateConnectionBar();
    }

    private void UpdateConnectionBar()
    {
        var st = SelectedConnection?.State
                 ?? Connections.Select(c => c.State).FirstOrDefault(s => s?.Status == "connected");
        bool connected = st?.Status == "connected";
        string host = !string.IsNullOrEmpty(st?.Host) ? st!.Host : (st?.Sysinfo?.HostName ?? "");

        ConnectionSummary = connected
            ? $"{(string.IsNullOrEmpty(host) ? "agent" : host)}   ·   ✓ agent connected"
            : Connections.Count == 0 ? "No RDP connection." : "⚠ No agent in this session.";

        if (connected && _lastAgentUpdate is { } t)
        {
            int secs = (int)Math.Max(0, (DateTime.UtcNow - t).TotalSeconds);
            Freshness = secs <= 1 ? "updated just now" : $"updated {secs}s ago";
            LivenessBrush = secs < 6 ? LiveBrush : StaleBrush;
        }
        else
        {
            Freshness = "";
            LivenessBrush = StaleBrush;
        }
    }

    /// <summary>Live metrics + a health rollup for one connection row, so the whole fleet is legible
    /// at a glance on the dashboard without selecting each one.</summary>
    private static void UpdateRowMetrics(ConnectionRow row)
    {
        var s = row.State;
        row.Cpu = s?.Sysinfo is { } si ? $"{si.CpuPercent:0}%" : "";
        row.Rtt = s?.Link is { RttMsLast: > 0 } lk ? $"{lk.RttMsLast:0.0} ms" : "";
        row.Throughput = s?.Counters is { } co ? Rate(co.Channels.Sum(c => c.SendRateBps + c.RecvRateBps)) : "";

        if (s is null || s.Status != "connected")
        {
            row.HealthBrush = UiBrushes.Muted;
            row.HealthText = "no agent";
            return;
        }

        var problems = new List<string>();
        if (s.Link is { PingTimeouts: > 0 } l) problems.Add($"{l.PingTimeouts}/{l.Pings} pings lost");
        int badDev = s.System?.Devices.Count(d => d.Problem.Length > 0) ?? 0;
        if (badDev > 0) problems.Add($"{badDev} device problem{(badDev > 1 ? "s" : "")}");
        if (s.Sysinfo is { CpuPercent: >= 90 }) problems.Add("CPU pegged");

        row.HealthBrush = problems.Count == 0 ? UiBrushes.Ok : UiBrushes.Warn;
        row.HealthText = problems.Count == 0 ? "healthy" : string.Join(" · ", problems);
    }

    private static BrokerServer.AgentState? Correlate(RdpWindow w, int windowCount, IReadOnlyList<BrokerServer.AgentState> states, out string agentText)
    {
        var connected = states.Where(s => s.Status == "connected").ToList();
        var byHost = connected.FirstOrDefault(s => !string.IsNullOrEmpty(s.Host) && s.Host.Equals(w.Host, StringComparison.OrdinalIgnoreCase));
        if (byHost is not null) { agentText = $"✓ {byHost.Host}"; return byHost; }
        if (windowCount == 1 && connected.Count == 1)
        {
            agentText = $"✓ {(string.IsNullOrEmpty(connected[0].Host) ? "connected" : connected[0].Host)}";
            return connected[0];
        }
        int awaiting = states.Count(s => s.Status == "listening");
        agentText = (windowCount == 1 && connected.Count == 0 && awaiting > 0) ? "⚠ no agent" : "—";
        return null;
    }

    private void UpdateDetails()
    {
        var st = SelectedConnection?.State
                 ?? Connections.Select(c => c.State).FirstOrDefault(s => s?.Status == "connected");

        if (st?.Sysinfo is { } s)
        {
            double upDays = s.UptimeMs / 1000.0 / 86400.0;
            double usedGb = (s.MemTotalBytes - s.MemAvailBytes) / 1024.0 / 1024 / 1024;
            double totGb = s.MemTotalBytes / 1024.0 / 1024 / 1024;
            HostHeader =
                $"{s.HostName}   ·   {s.OsProductName} {s.OsDisplayVer} (build {s.OsBuild}.{s.OsUbr})\n" +
                $"{s.CpuName}  ·  {s.CpuLogical} logical  ·  {s.CpuPercent:0.0}% CPU  ·  " +
                $"{usedGb:0.0}/{totGb:0.0} GB RAM  ·  up {upDays:0.0} d";
        }
        else
        {
            HostHeader = st is null ? "No connected agent." : "Waiting for host info…";
        }

        Processes.Clear();
        if (st?.Procs is { } p)
            foreach (var proc in p.Processes.OrderByDescending(x => x.WorkingSet).Take(200))
                Processes.Add(new ProcRow(proc.Pid, proc.ImageName, proc.UserName, $"{proc.WorkingSet / 1024 / 1024} MB"));

        Network.Clear();
        if (st?.Net is { } net)
            foreach (var e in net.Entries.OrderBy(e => e.State != "LISTEN").ThenBy(e => e.Local))
                Network.Add(new NetRow(e.Protocol, e.Local, e.Remote, e.State,
                    string.IsNullOrEmpty(e.Process) ? e.Pid.ToString() : $"{e.Process} ({e.Pid})"));

        Sessions.Clear();
        if (st?.Sessions is { } sess)
            foreach (var se in sess.Sessions)
                Sessions.Add(new SessionRow(se.SessionId, se.Station, se.User, se.State, se.ClientName));

        Services.Clear();
        if (st?.Services is { } svc)
            foreach (var sv in svc.Services.OrderBy(x => x.Name))
                Services.Add(new ServiceRow(sv.Name, sv.Status, sv.StartType, sv.Display));

        // An older agent leaves group empty; treat that as host vitals.
        PerfText = st?.Perf is { } perf
            ? string.Join("      ", perf.Counters
                .Where(c => c.Group is "" or "host")
                .Select(c => $"{c.Name}: {c.Value}{(string.IsNullOrEmpty(c.Unit) ? "" : " " + c.Unit)}"))
            : "";

        UpdateDvcTraffic(st?.Counters);
        UpdateLinkQuality(st?.Perf);
        UpdateClientMeasured(st?.Link);
        UpdateRttComparison(st?.Counters, st?.Link);
        UpdateSystemDetail(st?.System);
    }

    private void UpdateSystemDetail(SystemDetail? d)
    {
        Hotfixes.Clear();
        Gpus.Clear();
        Devices.Clear();
        if (d is null)
        {
            SystemBuild = "Waiting for the agent's system detail (collected every few seconds)…";
            return;
        }

        string boot = d.BootTimeTicks > 0
            ? new DateTime(d.BootTimeTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "—";
        double upDays = d.UptimeMs / 1000.0 / 86400.0;
        SystemBuild =
            $"BuildLabEx :  {d.BuildLabEx}\n" +
            $"Edition    :  {d.EditionId}    ·    {d.DisplayVersion}\n" +
            $"Installed  :  {d.InstallDate}    ·    owner {d.RegisteredOwner}\n" +
            $"Booted     :  {boot}    ·    up {upDays:0.0} d";
        if (d.Notes.Count > 0) SystemBuild += "\n\nCollector notes: " + string.Join("; ", d.Notes);

        foreach (var h in d.Hotfixes) Hotfixes.Add(new HotfixRow(h.HotfixId, h.Description, h.InstalledOn));
        foreach (var g in d.Gpus)
            Gpus.Add(new GpuRow(g.Name, g.DriverVersion, g.DriverDate, g.VramBytes > 0 ? Bytes(g.VramBytes) : "", g.Status));
        foreach (var p in d.Devices)
            Devices.Add(new DeviceRow(p.Name, p.DeviceClass, p.Status, p.Problem, p.Problem.Length > 0));
    }

    /// <summary>
    /// The one genuinely two-sided number: the agent times the channel round-trip on the session
    /// host (network only), the plugin times the same channel locally (network + whatever is
    /// queueing in the DVC). The gap is that queueing — the whole point of showing both.
    /// </summary>
    private void UpdateRttComparison(CounterSample? sample, ClientLink? link)
    {
        var ch = link is null ? null : sample?.Channels.FirstOrDefault(c => c.Name == link.Channel);
        double? server = ch is { RttMs: > 0 } ? ch.RttMs : null;
        double? client = link is { RttMsLast: > 0 } ? link.RttMsLast : null;

        RttServer = server is { } s ? $"{s:0.0} ms" : "—";
        RttClient = client is { } c ? $"{c:0.0} ms" : "—";

        if (server is { } sv && client is { } cl)
        {
            double gap = cl - sv;
            RttGap = $"{gap:0.0} ms";
            RttExplain = gap > 0.2
                ? "The plugin sees the network plus time spent queueing inside the DVC; the agent sees only the network. The gap is that DVC/queueing overhead."
                : "Client and server round-trips agree — negligible DVC queueing right now.";
        }
        else
        {
            RttGap = "—";
            RttExplain = client is null
                ? "Waiting for the plugin's own round-trip measurement of the diagnostics channel."
                : "The agent isn't reporting a per-channel RTT for this channel (the perfmon set may not expose one), so only the client-side number is available.";
        }
    }

    /// <summary>
    /// RemoteFX Network + Graphics, collected by the agent because rdpcorets.dll only
    /// instances those counters on the machine hosting the session.
    /// </summary>
    private void UpdateLinkQuality(PerfSnapshot? perf)
    {
        LinkQuality.Clear();

        var counters = perf?.Counters.Where(c => c.Group is "link" or "graphics").ToList();
        if (counters is null || counters.Count == 0)
        {
            LinkNote = perf is null
                ? "Waiting for the agent…"
                : "The RemoteFX counter sets reported no instances. They exist only on the session " +
                  "host, and only while a session is active.";
            return;
        }

        foreach (var c in counters)
            LinkQuality.Add(new LinkRow(
                c.Name,
                $"{c.Value}{(string.IsNullOrEmpty(c.Unit) ? "" : " " + c.Unit)}",
                c.Instance));

        LinkNote = "Measured on the session host (RemoteFX Network / Graphics). Units are as " +
                   "Windows reports them — the counter set does not document them consistently.";
    }

    /// <summary>
    /// What the plugin measured for itself, to sit next to the agent's numbers for the
    /// same channel. Divergence is the point: server RTT is the network, this is the
    /// network plus whatever is queueing in the DVC.
    /// </summary>
    private void UpdateClientMeasured(ClientLink? link)
    {
        if (link is null)
        {
            ClientMeasured = "";
            return;
        }

        string loss = link.PingTimeouts > 0 ? $"  ·  {link.PingTimeouts}/{link.Pings} pings lost" : "";
        ClientMeasured =
            $"Client-measured on {link.Channel}:  RTT {link.RttMsLast:0.0} ms " +
            $"(min {link.RttMsMin:0.0}, avg {link.RttMsAvg:0.0})  ·  " +
            $"sent {Bytes(link.BytesSent)}  ·  received {Bytes(link.BytesReceived)}{loss}";
    }

    /// <summary>
    /// Per-channel traffic measured by the agent on the session host. Direction is from
    /// the server's point of view: "sent" is host → client.
    /// </summary>
    partial void OnSelectedDvcChannelChanged(DvcRow? value)
    {
        if (value is not null) _selectedChannelName = value.Name;   // survive table rebuilds
        if (value is null)
        {
            ChannelDetail = "Select a channel above to see its config and full counters.";
            return;
        }

        var st = ConnectedAgent();
        var c = st?.Counters?.Channels.FirstOrDefault(x => x.Name == value.Name);
        var cfg = Channels.FirstOrDefault(x => x.Name == value.Name);
        bool isDiag = value.Name.Contains("diag::", StringComparison.OrdinalIgnoreCase);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{value.Name}");
        sb.AppendLine(cfg is not null
            ? $"Client config :  {cfg.Kind}  ·  {cfg.Activation}  ·  {cfg.Module}"
            : "Client config :  built-in, or not registered as a client-side plugin on this machine");
        if (c is not null)
        {
            sb.AppendLine($"Traffic       :  sent {Bytes(c.BytesSent)}  ·  received {Bytes(c.BytesReceived)}");
            sb.AppendLine($"Rates         :  {Rate(c.SendRateBps)} up  ·  {Rate(c.RecvRateBps)} down");
            string rtt = c.RttMs > 0 ? $"RTT {c.RttMs:0.#} ms" : "RTT —";
            string bw = c.BandwidthKbps > 0 ? $"  ·  bandwidth {c.BandwidthKbps:0} kbps" : "";
            sb.AppendLine($"Health        :  {rtt}{bw}  ·  {c.CurrentlyOpen} open instance(s)");
        }
        sb.Append(isDiag
            ? "Frames        :  this is a diagnostics channel — its per-frame feed is on the Frames tab."
            : "Frames        :  not tapped (the inspector only frames the diagnostics channel).");
        ChannelDetail = sb.ToString();
    }

    private void UpdateDvcTraffic(CounterSample? sample)
    {
        string keep = _selectedChannelName;   // Clear() nulls the selection; re-select by name below
        DvcTraffic.Clear();
        if (sample is null)
        {
            DvcNote = "Waiting for the agent to report channel traffic…";
            return;
        }

        foreach (var c in sample.Channels.OrderByDescending(c => c.BytesSent + c.BytesReceived))
            DvcTraffic.Add(new DvcRow(
                c.Name,
                Bytes(c.BytesSent),
                Bytes(c.BytesReceived),
                Rate(c.SendRateBps),
                Rate(c.RecvRateBps),
                c.RttMs > 0 ? $"{c.RttMs:0.#} ms" : "—"));

        // Restore the drill-down selection (and refresh its detail with the new numbers).
        if (!string.IsNullOrEmpty(keep))
            SelectedDvcChannel = DvcTraffic.FirstOrDefault(r => r.Name == keep);

        string source = sample.Source switch
        {
            "perfmon" => "Source: “Remote Desktop Virtual Channel” counters on the session host.",
            "etw" => "Source: RDP server ETW write-flush events — send direction only, and partial.",
            _ => "",
        };
        DvcNote = string.Join("  ", new[] { sample.Note, source }.Where(s => !string.IsNullOrEmpty(s)));
    }

    private static string Bytes(ulong n) =>
        n >= 1024UL * 1024 * 1024 ? $"{n / 1024.0 / 1024 / 1024:0.00} GB" :
        n >= 1024UL * 1024 ? $"{n / 1024.0 / 1024:0.00} MB" :
        n >= 1024UL ? $"{n / 1024.0:0.0} KB" : $"{n} B";

    private static string Rate(double bytesPerSecond) =>
        double.IsFinite(bytesPerSecond) && bytesPerSecond >= 1 ? $"{Bytes((ulong)bytesPerSecond)}/s" : "—";

    private void UpdateChannels()
    {
        var configs = ClientChannels.Collect();
        Channels.Clear();
        foreach (var c in configs)
            Channels.Add(new ChannelRow(
                c.Name,
                c.IsBuiltin ? "built-in" : (c.Activation.Contains("Server") ? "plugin" : c.Activation),
                c.Activation,
                string.IsNullOrEmpty(c.ModulePath) ? c.Description : $"{c.ModulePath}{(string.IsNullOrEmpty(c.Bitness) ? "" : $" ({c.Bitness})")}",
                c.Clsid));
    }

    private void UpdateStatus(int windowCount, IReadOnlyList<BrokerServer.AgentState> states)
    {
        int connected = states.Count(s => s.Status == "connected");
        int awaiting = states.Count(s => s.Status == "listening");
        Status = connected > 0 ? $"Agent connected on {connected} session(s)."
               : awaiting > 0 ? $"⚠ No agent on {awaiting} session(s) — copy the install command into that session (one-time)."
               // Elevated with nothing reporting is almost always the cause, not a
               // coincidence: the plugin runs at medium integrity under mstsc and
               // cannot write to a broker pipe owned by an elevated process.
               : Elevated ? "Running as administrator — the plugin (medium integrity) can't reach the broker. Restart the companion NOT as admin."
               : windowCount == 0 ? "No RDP windows open. Connect with mstsc."
               : "Waiting for the RDPeek plugin to report…";
    }

    private static readonly bool Elevated = IsElevated();

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
