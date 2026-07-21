using System.Drawing;
using System.Security.Principal;
using System.Windows.Forms;

namespace Rdpeek.Companion;

/// <summary>
/// RDPeek dashboard: a connection list on top (host, agent status, window), and below,
/// the selected connection's host header + live process table — data pushed from the
/// plugin (which polls the agent over the DVC) via the broker.
/// </summary>
public sealed class MainForm : Form
{
    private const string InstallCommand =
        "irm https://raw.githubusercontent.com/guscatalano/RDPeek/main/tools/install-agent-web.ps1 | iex";

    private readonly DataGridView _connGrid = new();
    private readonly Label _hostHeader = new();
    private readonly DataGridView _procGrid = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _refresh = new();
    private readonly BrokerServer _broker = new();
    private readonly bool _elevated = IsElevated();

    private IReadOnlyList<BrokerServer.AgentState> _states = new List<BrokerServer.AgentState>();
    private string? _selectedKey;

    public MainForm()
    {
        Text = "RDPeek Companion";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(820, 560);
        MinimumSize = new Size(600, 400);

        var copyBtn = new Button { Text = "Copy agent-install command", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(4), Padding = new Padding(8, 4, 8, 4) };
        copyBtn.Click += OnCopyInstall;
        var refreshBtn = new Button { Text = "Refresh", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(4), Padding = new Padding(12, 4, 12, 4) };
        refreshBtn.Click += (_, _) => RefreshUi();

        var topBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(4) };
        topBar.Controls.Add(copyBtn);
        topBar.Controls.Add(refreshBtn);

        // --- connections list ---
        _connGrid.Dock = DockStyle.Fill;
        _connGrid.AllowUserToAddRows = false;
        _connGrid.AllowUserToDeleteRows = false;
        _connGrid.ReadOnly = true;
        _connGrid.RowHeadersVisible = false;
        _connGrid.MultiSelect = false;
        _connGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _connGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _connGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Host", HeaderText = "Host", FillWeight = 28 });
        _connGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Agent", HeaderText = "Agent", FillWeight = 30 });
        _connGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Window", HeaderText = "Window", FillWeight = 42 });
        _connGrid.SelectionChanged += (_, _) => OnSelectionChanged();

        // --- details: host header + process table ---
        _hostHeader.Dock = DockStyle.Fill;
        _hostHeader.Font = new Font(FontFamily.GenericMonospace, 9f);
        _hostHeader.Padding = new Padding(6, 4, 6, 4);
        _hostHeader.Text = "Select a connection.";

        _procGrid.Dock = DockStyle.Fill;
        _procGrid.AllowUserToAddRows = false;
        _procGrid.ReadOnly = true;
        _procGrid.RowHeadersVisible = false;
        _procGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _procGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Pid", HeaderText = "PID", FillWeight = 12 });
        _procGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Image", HeaderText = "Image", FillWeight = 34 });
        _procGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "User", HeaderText = "User", FillWeight = 34 });
        _procGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Mem", HeaderText = "Working set", FillWeight = 20 });

        var details = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        details.Controls.Add(_hostHeader, 0, 0);
        details.Controls.Add(_procGrid, 0, 1);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 150 };
        split.Panel1.Controls.Add(_connGrid);
        split.Panel2.Controls.Add(details);

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(6, 0, 6, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        layout.Controls.Add(topBar, 0, 0);
        layout.Controls.Add(split, 0, 1);
        layout.Controls.Add(_status, 0, 2);
        Controls.Add(layout);

        _broker.Changed += () => { if (IsHandleCreated) BeginInvoke(RefreshUi); };
        _broker.Start();

        _refresh.Interval = 2000;
        _refresh.Tick += (_, _) => RefreshUi();
        _refresh.Start();
        RefreshUi();
    }

    private void OnCopyInstall(object? sender, EventArgs e)
    {
        try
        {
            Clipboard.SetText(InstallCommand);
            _status.ForeColor = SystemColors.ControlText;
            _status.Text = "Install command copied. Paste it into a PowerShell in the remote session and press Enter (one-time).";
        }
        catch (Exception ex)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = $"Couldn't copy: {ex.Message}";
        }
    }

    private void RefreshUi()
    {
        var windows = RdpWindows.Enumerate();
        _states = _broker.Snapshot();

        _connGrid.Rows.Clear();
        foreach (var w in windows)
        {
            string agent = AgentStatusFor(w, windows.Count);
            int row = _connGrid.Rows.Add(w.Host, agent, w.Title);
            if (agent.StartsWith("⚠")) _connGrid.Rows[row].Cells["Agent"].Style.ForeColor = Color.DarkOrange;
            else if (agent.StartsWith("✓")) _connGrid.Rows[row].Cells["Agent"].Style.ForeColor = Color.Green;
        }

        UpdateSummary(windows.Count);
        UpdateDetails();
    }

    private string AgentStatusFor(RdpWindow w, int windowCount)
    {
        var connected = _states.Where(s => s.Status == "connected").ToList();
        var byHost = connected.FirstOrDefault(s => !string.IsNullOrEmpty(s.Host) && s.Host.Equals(w.Host, StringComparison.OrdinalIgnoreCase));
        if (byHost is not null) return $"✓ {byHost.Host}";
        if (windowCount == 1 && connected.Count == 1)
            return $"✓ {(string.IsNullOrEmpty(connected[0].Host) ? "connected" : connected[0].Host)}";
        int awaiting = _states.Count(s => s.Status == "listening");
        if (windowCount == 1 && connected.Count == 0 && awaiting > 0) return "⚠ no agent";
        return "—";
    }

    /// <summary>Choose which connection's details to show: the selected one, else the only connected one.</summary>
    private BrokerServer.AgentState? SelectedState()
    {
        if (_selectedKey is not null)
        {
            var match = _states.FirstOrDefault(s => $"{s.Pid}:{s.Seq}" == _selectedKey);
            if (match is not null) return match;
        }
        var connected = _states.Where(s => s.Status == "connected").ToList();
        return connected.Count == 1 ? connected[0] : connected.FirstOrDefault();
    }

    private void OnSelectionChanged()
    {
        if (_connGrid.SelectedRows.Count == 0) return;
        // Map the selected row's host to a connected state, if any.
        var host = _connGrid.SelectedRows[0].Cells["Host"].Value?.ToString() ?? "";
        var st = _states.FirstOrDefault(s => s.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && s.Status == "connected")
                 ?? _states.FirstOrDefault(s => s.Status == "connected");
        _selectedKey = st is null ? null : $"{st.Pid}:{st.Seq}";
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        var st = SelectedState();
        if (st?.Sysinfo is { } s)
        {
            double upDays = s.UptimeMs / 1000.0 / 86400.0;
            double memUsedGb = (s.MemTotalBytes - s.MemAvailBytes) / 1024.0 / 1024 / 1024;
            double memTotGb = s.MemTotalBytes / 1024.0 / 1024 / 1024;
            _hostHeader.Text =
                $"{s.HostName}   {s.OsProductName} {s.OsDisplayVer} (build {s.OsBuild}.{s.OsUbr}){Environment.NewLine}" +
                $"CPU  {s.CpuName}  ·  {s.CpuLogical} logical  ·  {s.CpuPercent:0.0}%{Environment.NewLine}" +
                $"RAM  {memUsedGb:0.0} / {memTotGb:0.0} GB   ·   uptime {upDays:0.0} d   ·   session {s.SessionId} ({s.Protocol})";
        }
        else
        {
            _hostHeader.Text = st is null ? "No connected agent." : "Waiting for host info…";
        }

        _procGrid.Rows.Clear();
        if (st?.Procs is { } p)
        {
            foreach (var proc in p.Processes.OrderByDescending(x => x.WorkingSet).Take(200))
                _procGrid.Rows.Add(proc.Pid, proc.ImageName, proc.UserName, $"{proc.WorkingSet / 1024 / 1024} MB");
        }
    }

    private void UpdateSummary(int windowCount)
    {
        int awaiting = _states.Count(s => s.Status == "listening");
        int connected = _states.Count(s => s.Status == "connected");

        if (connected > 0)
        {
            _status.ForeColor = Color.Green;
            _status.Text = $"Agent connected on {connected} session(s).";
        }
        else if (awaiting > 0)
        {
            _status.ForeColor = Color.DarkOrange;
            _status.Text = $"⚠ No agent on {awaiting} session(s). Copy the install command and paste it into that session (one-time).";
        }
        else if (_elevated)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = "Running as administrator — the plugin (medium integrity) can't reach the broker. Restart the companion NOT as admin.";
        }
        else
        {
            _status.ForeColor = SystemColors.ControlText;
            _status.Text = windowCount == 0 ? "No RDP windows open. Connect with mstsc." : "Waiting for the RDPeek plugin to report…";
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _broker.Dispose();
        base.OnFormClosed(e);
    }

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
