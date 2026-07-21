using System.Windows.Forms;

namespace Rdpeek.Companion;

/// <summary>
/// Multi-connection aware status view: one row per open RDP window showing whether its
/// RDPeek agent is connected (via the broker). A single button copies the one-time
/// install command to the clipboard — paste it into the remote session once, and every
/// future connect auto-starts the agent.
/// </summary>
public sealed class MainForm : Form
{
    // Paste this into a PowerShell inside the remote session (one-time per machine).
    private const string InstallCommand =
        "irm https://raw.githubusercontent.com/guscatalano/RDPeek/main/tools/install-agent-web.ps1 | iex";

    private readonly DataGridView _grid = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _refresh = new();
    private readonly BrokerServer _broker = new();
    private List<RdpWindow> _windows = new();

    public MainForm()
    {
        Text = "RDPeek Companion";
        Width = 760;
        Height = 360;
        MinimumSize = new Size(560, 240);

        var copyBtn = new Button
        {
            Text = "Copy agent-install command",
            Left = 10, Top = 10, Width = 220, Height = 28,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };
        copyBtn.Click += OnCopyInstall;

        var refreshBtn = new Button { Text = "Refresh", Left = 660, Top = 10, Width = 80, Height = 28, Anchor = AnchorStyles.Top | AnchorStyles.Right };

        var hint = new Label
        {
            Text = "Paste it into a PowerShell in the remote session, once. Then every connect auto-starts the agent.",
            Left = 240, Top = 15, Width = 410, Height = 30, AutoSize = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };
        refreshBtn.Click += (_, _) => RefreshUi();

        _grid.Left = 10;
        _grid.Top = 48;
        _grid.Width = 730;
        _grid.Height = 240;
        _grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Host", HeaderText = "Host", FillWeight = 28 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Agent", HeaderText = "Agent", FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Title", HeaderText = "Window", FillWeight = 42 });

        _status.Left = 12;
        _status.Top = 296;
        _status.Width = 730;
        _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _status.Text = "Ready.";

        Controls.AddRange(new Control[] { copyBtn, hint, refreshBtn, _grid, _status });

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
        var connected = states.FirstOrDefault(s =>
            s.Status == "connected" && !string.IsNullOrEmpty(s.Host) &&
            s.Host.Equals(w.Host, StringComparison.OrdinalIgnoreCase));
        if (connected is not null) return $"✓ {connected.Host}";

        int awaiting = states.Count(s => s.Status == "listening");
        if (awaiting > 0 && _windows.Count == 1) return "⚠ no agent";
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
