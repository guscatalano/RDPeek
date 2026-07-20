using System.Windows.Forms;

namespace Rdpeek.Companion;

/// <summary>
/// Multi-connection aware: one row per open RDP window, each with its own
/// "Copy & run agent" button that targets that specific session.
/// </summary>
public sealed class MainForm : Form
{
    private readonly DataGridView _grid = new();
    private readonly TextBox _agentPath = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _refresh = new();
    private List<RdpWindow> _windows = new();

    public MainForm()
    {
        Text = "RDPeek Companion";
        Width = 760;
        Height = 380;
        MinimumSize = new Size(560, 260);

        var pathLabel = new Label { Text = "Agent exe:", AutoSize = true, Left = 10, Top = 14 };
        _agentPath.Left = 80;
        _agentPath.Top = 10;
        _agentPath.Width = 560;
        _agentPath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _agentPath.Text = DefaultAgentPath();

        var refreshBtn = new Button { Text = "Refresh", Left = 650, Top = 9, Width = 80, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        refreshBtn.Click += (_, _) => Reload();

        _grid.Left = 10;
        _grid.Top = 44;
        _grid.Width = 720;
        _grid.Height = 260;
        _grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Host", HeaderText = "Host", FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Title", HeaderText = "Window", FillWeight = 45 });
        _grid.Columns.Add(new DataGridViewButtonColumn { Name = "Action", HeaderText = "", Text = "Copy & run agent", UseColumnTextForButtonValue = true, FillWeight = 25 });
        _grid.CellContentClick += OnCellClick;

        _status.Left = 12;
        _status.Top = 312;
        _status.Width = 720;
        _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _status.Text = "Ready.";

        Controls.AddRange(new Control[] { pathLabel, _agentPath, refreshBtn, _grid, _status });

        _refresh.Interval = 3000;
        _refresh.Tick += (_, _) => Reload();
        _refresh.Start();
        Reload();
    }

    private void Reload()
    {
        _windows = RdpWindows.Enumerate();
        _grid.Rows.Clear();
        foreach (var w in _windows)
            _grid.Rows.Add(w.Host, w.Title);

        if (_windows.Count == 0)
            _status.Text = "No RDP windows open. Connect with mstsc, then Refresh.";
    }

    private async void OnCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _windows.Count) return;
        if (_grid.Columns[e.ColumnIndex].Name != "Action") return;

        var w = _windows[e.RowIndex];
        var path = _agentPath.Text.Trim();
        if (!File.Exists(path))
        {
            _status.Text = $"Agent exe not found: {path}";
            return;
        }

        _status.Text = $"Bootstrapping agent in '{w.Host}' — sending Win+R…";
        try
        {
            await InputBootstrap.RunAsync(w.Hwnd, path);
            _status.Text = $"Sent to '{w.Host}'. Watch %TEMP%\\rdpeek-plugin.log for the channel connection.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Failed: {ex.Message}";
        }
    }

    private static string DefaultAgentPath()
    {
        // Prefer a published self-contained agent; fall back to the debug build.
        var baseDir = AppContext.BaseDirectory;
        // src/Rdpeek.Companion/bin/... → repo root is five levels up in dev layouts; just try a couple known spots.
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
