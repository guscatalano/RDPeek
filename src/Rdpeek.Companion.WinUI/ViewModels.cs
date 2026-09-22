using System.Collections.ObjectModel;
using System.Security.Principal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dvc.Diag.Protocol;
using Microsoft.UI.Dispatching;
using Rdpeek.Client;
using Windows.ApplicationModel.DataTransfer;

namespace Rdpeek.Companion.WinUI;

/// <summary>One RDP connection (an mstsc window) + its correlated agent state.</summary>
public partial class ConnectionRow : ObservableObject
{
    public IntPtr Hwnd { get; init; }
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _agent = "—";
    [ObservableProperty] private string _window = "";
    public BrokerServer.AgentState? State { get; set; }
}

public sealed record ProcRow(uint Pid, string Image, string User, string Mem);

public sealed record ChannelRow(string Name, string Kind, string Activation, string Module, string Clsid);

public sealed record NetRow(string Proto, string Local, string Remote, string State, string Process);

public sealed record SessionRow(uint Id, string Station, string User, string State, string Client);

public sealed record ServiceRow(string Name, string Status, string StartType, string Display);

/// <summary>Live traffic on one remote DVC, as measured on the session host.</summary>
public sealed record DvcRow(string Name, string Sent, string Received, string SendRate, string RecvRate, string Rtt);

/// <summary>One RemoteFX link/graphics counter, as Windows names it.</summary>
public sealed record LinkRow(string Name, string Value, string Instance);

/// <summary>A flagged frame the inspector saw on the channel.</summary>
public sealed record FrameAnomalyRow(string Time, string Direction, string Type, string Detail);

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

    [ObservableProperty] private ConnectionRow? _selectedConnection;
    [ObservableProperty] private string _hostHeader = "Connect an RDP session to see host details.";
    [ObservableProperty] private string _perfText = "";
    [ObservableProperty] private string _dvcNote = "Waiting for the agent to report channel traffic…";
    [ObservableProperty] private string _clientMeasured = "";
    [ObservableProperty] private string _linkNote = "";
    [ObservableProperty] private string _status = "Starting…";

    // File pull (dashboard → plugin → agent → local disk).
    [ObservableProperty] private string _remotePath = "";
    [ObservableProperty] private string _localDest = "";
    [ObservableProperty] private double _pullProgress;      // 0..100
    [ObservableProperty] private string _pullStatus = "Pull a file from the remote session onto this machine.";

    // Frame inspector.
    [ObservableProperty] private string _frameStats = "No frames tapped yet — connect an agent.";
    public ObservableCollection<FrameAnomalyRow> FrameAnomalies { get; } = new();

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _broker.Changed += () => _dispatcher.TryEnqueue(Refresh);
        _broker.PullUpdate += (kind, payload) => _dispatcher.TryEnqueue(() => OnPullUpdate(kind, payload));
        _broker.FrameUpdate += (kind, payload) => _dispatcher.TryEnqueue(() => OnFrameUpdate(kind, payload));
        _broker.Start();

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    partial void OnSelectedConnectionChanged(ConnectionRow? value) => UpdateDetails();

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

        if (_broker.SendCommand(st.Pid, Broker.Format("pull", 0, 0, $"{RemotePath}\t{LocalDest}")))
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

    private void OnFrameUpdate(string kind, string payload)
    {
        var p = payload.Split('\t');
        if (kind == "framestats" && p.Length >= 3)
        {
            FrameStats = $"{p[0]} frames in · {p[1]} out · {p[2]} anomalies";
        }
        else if (kind == "frameanomaly" && p.Length >= 3)
        {
            FrameAnomalies.Insert(0, new FrameAnomalyRow(DateTime.Now.ToString("HH:mm:ss"), p[0], p[1], p[2]));
            while (FrameAnomalies.Count > 200) FrameAnomalies.RemoveAt(FrameAnomalies.Count - 1);
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
        }
        for (int i = Connections.Count - 1; i >= 0; i--)
            if (!seen.Contains(Connections[i].Hwnd)) Connections.RemoveAt(i);

        if (SelectedConnection is null && Connections.Count > 0)
            SelectedConnection = Connections.FirstOrDefault(c => c.State?.Status == "connected") ?? Connections[0];

        UpdateDetails();
        UpdateChannels();
        UpdateStatus(windows.Count, states);
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
    private void UpdateDvcTraffic(CounterSample? sample)
    {
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
