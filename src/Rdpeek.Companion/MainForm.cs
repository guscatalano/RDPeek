using System.Windows.Forms;

namespace Rdpeek.Companion;

/// <summary>
/// Multi-connection aware: one row per open RDP window, each with its own
/// "Copy & run agent" button that targets that specific session. The broker lights up
/// an "Agent" status per connection so you see when a session has no agent — no logs.
/// </summary>
public sealed class MainForm : Form
{
    private readonly DataGridView _grid = new();
    private readonly TextBox _agentPath = new();
    private readonly CheckBox _winR = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _refresh = new();
    private readonly BrokerServer _broker = new();
    private List<RdpWindow> _windows = new();

    public MainForm()
    {
        Text = "RDPeek Companion";
        Width = 780;
        Height = 380;
        MinimumSize = new Size(580, 260);

        var pathLabel = new Label { Text = "Agent exe:", AutoSize = true, Left = 10, Top = 14 };
        _agentPath.Left = 80;
        _agentPath.Top = 10;
        _agentPath.Width = 580;
        _agentPath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _agentPath.Text = DefaultAgentPath();

        var refreshBtn = new Button { Text = "Refresh", Left = 670, Top = 9, Width = 80, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        refreshBtn.Click += (_, _) => RefreshUi();

        _winR.Left = 80;
        _winR.Top = 40;
        _winR.Width = 660;
        _winR.AutoSize = false;
        _winR.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _winR.Checked = true; // default: open the session's Run dialog with Win+R
        _winR.Text = "Use Win+R to open Run in the session (set mstsc Keyboard → “On the remote computer”, or go full-screen). " +
                     "Unchecked: type into a shell you focused in the session. Needs Drives redirection for \\\\tsclient.";

        _grid.Left = 10;
        _grid.Top = 70;
        _grid.Width = 740;
        _grid.Height = 234;
        _grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Host", HeaderText = "Host", FillWeight = 26 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Agent", HeaderText = "Agent", FillWeight = 24 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Title", HeaderText = "Window", FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewButtonColumn { Name = "Action", HeaderText = "", Text = "Copy & run agent", UseColumnTextForButtonValue = true, FillWeight = 20 });
        _grid.CellContentClick += OnCellClick;

        _status.Left = 12;
        _status.Top = 314;
        _status.Width = 740;
        _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _status.Text = "Ready.";

        Controls.AddRange(new Control[] { pathLabel, _agentPath, refreshBtn, _winR, _grid, _status });

        _broker.Changed += OnBrokerChanged;
        _broker.Start();

        _refresh.Interval = 3000;
        _refresh.Tick += (_, _) => RefreshUi();
        _refresh.Start();
        RefreshUi();
    }

    private void OnBrokerChanged()
    {
        // Broker events arrive on a background thread — marshal to the UI.
        if (IsHandleCreated)
            BeginInvoke(RefreshUi);
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
            if (agent.StartsWith("⚠"))
                _grid.Rows[row].Cells["Agent"].Style.ForeColor = Color.DarkOrange;
            else if (agent.StartsWith("✓"))
                _grid.Rows[row].Cells["Agent"].Style.ForeColor = Color.Green;
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
        // If there's a single window and a plugin awaiting an agent, it's this one.
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
            _status.Text = $"⚠ Agent not detected on {awaiting} session(s) — pick its window and click “Copy & run agent”.";
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
                : "Waiting for the RDPeek plugin to report (connect a session with the plugin registered).";
        }
    }

    private async void OnCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _windows.Count) return;
        if (_grid.Columns[e.ColumnIndex].Name != "Action") return;

        var w = _windows[e.RowIndex];
        var path = _agentPath.Text.Trim();
        if (!File.Exists(path))
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = $"Agent exe not found: {path}";
            return;
        }

        _status.ForeColor = SystemColors.ControlText;
        _status.Text = _winR.Checked
            ? $"Bootstrapping agent in '{w.Host}' — sending Win+R…"
            : $"Bootstrapping agent in '{w.Host}' — pasting into the focused session window…";
        try
        {
            bool focused = await InputBootstrap.RunAsync(w.Hwnd, path, _winR.Checked);
            if (focused)
            {
                _status.ForeColor = SystemColors.ControlText;
                _status.Text = $"Sent to '{w.Host}'. Watching for the agent to connect…";
            }
            else
            {
                _status.ForeColor = Color.Firebrick;
                _status.Text = $"Could not bring '{w.Host}' to the foreground — click the RDP window once, then retry.";
            }
        }
        catch (Exception ex)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = $"Failed: {ex.Message}";
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _broker.Dispose();
        base.OnFormClosed(e);
    }

    private static string DefaultAgentPath()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
        {
            Path.Combine(Environment.CurrentDirectory, "publish", "agent", "rdpeek-agent.exe"),
            Path.Combine(baseDir, "rdpeek-agent.exe"),
        })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return Path.Combine(Environment.CurrentDirectory, "publish", "agent", "rdpeek-agent.exe");
    }
}
