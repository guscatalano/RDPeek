using System.Drawing;
using System.Security.Principal;
using System.Windows.Forms;

namespace Rdpeek.Companion;

/// <summary>
/// Multi-connection aware status view: one row per open RDP window showing whether its
/// RDPeek agent is connected (via the broker). A button copies the one-time install
/// command to the clipboard — paste it into the remote session once, and every future
/// connect auto-starts the agent. Layout uses docking/auto-size so it scales at any DPI.
/// </summary>
public sealed class MainForm : Form
{
    private const string InstallCommand =
        "irm https://raw.githubusercontent.com/guscatalano/RDPeek/main/tools/install-agent-web.ps1 | iex";

    private readonly DataGridView _grid = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _refresh = new();
    private readonly BrokerServer _broker = new();
    private readonly bool _elevated = IsElevated();
    private List<RdpWindow> _windows = new();

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public MainForm()
    {
        Text = "RDPeek Companion";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 360);
        MinimumSize = new Size(520, 280);

        var copyBtn = new Button
        {
            Text = "Copy agent-install command",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(4),
            Padding = new Padding(8, 4, 8, 4),
        };
        copyBtn.Click += OnCopyInstall;

        var refreshBtn = new Button
        {
            Text = "Refresh",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(4),
            Padding = new Padding(12, 4, 12, 4),
        };
        refreshBtn.Click += (_, _) => RefreshUi();

        var topBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(4),
        };
        topBar.Controls.Add(copyBtn);
        topBar.Controls.Add(refreshBtn);

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Host", HeaderText = "Host", FillWeight = 28 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Agent", HeaderText = "Agent", FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Title", HeaderText = "Window", FillWeight = 42 });

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(6, 0, 6, 0);
        _status.Text = "Copy the install command, paste it into the remote session once, then connect.";

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        layout.Controls.Add(topBar, 0, 0);
        layout.Controls.Add(_grid, 0, 1);
        layout.Controls.Add(_status, 0, 2);
        Controls.Add(layout);

        _broker.Changed += OnBrokerChanged;
        _broker.Start();

        _refresh.Interval = 3000;
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

    private void OnBrokerChanged()
    {
        if (IsHandleCreated) BeginInvoke(RefreshUi);
    }

    private void RefreshUi()
    {
        _windows = RdpWindows.Enumerate();
        var states = _broker.Snapshot();

        _grid.Rows.Clear();
        foreach (var w in _windows)
        {
            string agent = AgentStatusFor(w, states);
            int row = _grid.Rows.Add(w.Host, agent, w.Title);
            if (agent.StartsWith("⚠")) _grid.Rows[row].Cells["Agent"].Style.ForeColor = Color.DarkOrange;
            else if (agent.StartsWith("✓")) _grid.Rows[row].Cells["Agent"].Style.ForeColor = Color.Green;
        }

        UpdateSummary(states);
    }

    private string AgentStatusFor(RdpWindow w, IReadOnlyList<BrokerServer.AgentState> states)
    {
        var connected = states.Where(s => s.Status == "connected").ToList();

        // Prefer an exact host match (window title host == remote machine name)…
        var byHost = connected.FirstOrDefault(s =>
            !string.IsNullOrEmpty(s.Host) && s.Host.Equals(w.Host, StringComparison.OrdinalIgnoreCase));
        if (byHost is not null) return $"✓ {byHost.Host}";

        // …but the mstsc window title (what you connected to — maybe an IP or saved
        // name) often differs from the remote machine name. With a single connection,
        // correlate directly.
        if (_windows.Count == 1 && connected.Count == 1)
            return $"✓ {(string.IsNullOrEmpty(connected[0].Host) ? "connected" : connected[0].Host)}";

        int awaiting = states.Count(s => s.Status == "listening");
        if (_windows.Count == 1 && connected.Count == 0 && awaiting > 0) return "⚠ no agent";
        return "—";
    }

    private void UpdateSummary(IReadOnlyList<BrokerServer.AgentState> states)
    {
        int awaiting = states.Count(s => s.Status == "listening");
        int connected = states.Count(s => s.Status == "connected");

        if (awaiting > 0)
        {
            _status.ForeColor = Color.DarkOrange;
            _status.Text = $"⚠ No agent on {awaiting} session(s). Copy the install command and paste it into that session (one-time).";
        }
        else if (connected > 0)
        {
            _status.ForeColor = Color.Green;
            _status.Text = $"Agent connected on {connected} session(s).";
        }
        else if (_elevated)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = "Running as administrator — the plugin (medium integrity) can't reach the broker. " +
                           "Restart the companion NOT as admin.";
        }
        else
        {
            _status.ForeColor = SystemColors.ControlText;
            _status.Text = _windows.Count == 0
                ? "No RDP windows open. Connect with mstsc."
                : "Waiting for the RDPeek plugin to report…";
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _broker.Dispose();
        base.OnFormClosed(e);
    }
}
