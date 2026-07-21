using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    [ObservableProperty] private ConnectionRow? _selectedConnection;
    [ObservableProperty] private string _hostHeader = "Connect an RDP session to see host details.";
    [ObservableProperty] private string _perfText = "";
    [ObservableProperty] private string _status = "Starting…";

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _broker.Changed += () => _dispatcher.TryEnqueue(Refresh);
        _broker.Start();

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    partial void OnSelectedConnectionChanged(ConnectionRow? value) => UpdateDetails();

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

        PerfText = st?.Perf is { } perf
            ? string.Join("      ", perf.Counters.Select(c => $"{c.Name}: {c.Value}{(string.IsNullOrEmpty(c.Unit) ? "" : " " + c.Unit)}"))
            : "";
    }

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
               : windowCount == 0 ? "No RDP windows open. Connect with mstsc."
               : "Waiting for the RDPeek plugin to report…";
    }
}
