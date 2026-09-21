using System.Windows.Forms;

namespace Rdpeek.Bootstrap;

/// <summary>
/// The installer window: collect a host + folder holding the agent and the install
/// script, host the RDP control, and provision the agent over a single redirected
/// connection whose "start program" runs the script in-session.
/// </summary>
internal sealed class BootstrapForm : Form
{
    private readonly TextBox _host = new() { Dock = DockStyle.Fill };
    private readonly TextBox _user = new() { Dock = DockStyle.Fill };
    private readonly TextBox _agentFolder = new() { Dock = DockStyle.Fill };
    private readonly TextBox _script = new() { Dock = DockStyle.Fill };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BackColor = System.Drawing.Color.Black,
        ForeColor = System.Drawing.Color.Gainsboro,
        Font = new System.Drawing.Font("Consolas", 9f),
    };
    private readonly Button _provision = new() { Text = "Provision agent", Dock = DockStyle.Fill };
    private readonly Button _disconnect = new() { Text = "Disconnect", Dock = DockStyle.Fill, Enabled = false };

    private readonly AxRdpClient _rdp = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 500 };

    private int _lastState = -1;
    private bool _reachedConnected;
    private bool _agentDetected;
    private string? _startProgram;
    private AgentWatch? _watch;

    public BootstrapForm()
    {
        Text = "RDPeek — agent installer";
        Width = 1100;
        Height = 780;
        StartPosition = FormStartPosition.CenterScreen;

        _rdp.SendToBack();

        Controls.Add(_rdp);            // Fill (added first / lowest z so it takes what's left)
        Controls.Add(BuildLogPanel()); // Bottom
        Controls.Add(BuildTopPanel());  // Top

        _provision.Click += OnProvision;
        _disconnect.Click += (_, _) => Disconnect("Disconnected by user.");
        _poll.Tick += OnPoll;
        FormClosing += (_, _) => { try { Script(_rdp.Control).Disconnect(); } catch { /* not connected */ } _watch?.Dispose(); };

        (_agentFolder.Text, _script.Text) = GuessDefaults();
    }

    // ---- UI construction ---------------------------------------------------

    private Control BuildTopPanel()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            RowCount = 4,
            Padding = new Padding(8),
            Height = 140,
            AutoSize = true,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));

        grid.Controls.Add(new Label { Text = "Host", Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft }, 0, 0);
        grid.Controls.Add(_host, 1, 0);
        grid.Controls.Add(new Label { Text = "Username", Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft }, 2, 0);
        grid.Controls.Add(_user, 3, 0);

        grid.Controls.Add(new Label { Text = "Agent folder", Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft }, 0, 1);
        grid.Controls.Add(_agentFolder, 1, 1);
        grid.Controls.Add(Browse("Browse…", BrowseAgentFolder), 3, 1);

        grid.Controls.Add(new Label { Text = "Install script", Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft }, 0, 2);
        grid.Controls.Add(_script, 1, 2);
        grid.Controls.Add(Browse("Browse…", BrowseScript), 3, 2);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Height = 34 };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        buttons.Controls.Add(_provision, 0, 0);
        buttons.Controls.Add(_disconnect, 1, 0);
        grid.Controls.Add(buttons, 1, 3);
        grid.SetColumnSpan(buttons, 3);

        return grid;
    }

    private Control BuildLogPanel()
    {
        var panel = new Panel { Dock = DockStyle.Bottom, Height = 150, Padding = new Padding(8, 0, 8, 8) };
        panel.Controls.Add(_log);
        return panel;
    }

    private static Button Browse(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, Dock = DockStyle.Fill };
        b.Click += onClick;
        return b;
    }

    private void BrowseAgentFolder(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = "Folder containing rdpeek-agent.exe" };
        if (Directory.Exists(_agentFolder.Text)) dlg.SelectedPath = _agentFolder.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) _agentFolder.Text = dlg.SelectedPath;
    }

    private void BrowseScript(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog { Filter = "PowerShell script (*.ps1)|*.ps1|All files (*.*)|*.*" };
        if (File.Exists(_script.Text)) dlg.FileName = _script.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) _script.Text = dlg.FileName;
    }

    // ---- provisioning ------------------------------------------------------

    private void OnProvision(object? sender, EventArgs e)
    {
        var host = _host.Text.Trim();
        var user = _user.Text.Trim();
        var folder = _agentFolder.Text.Trim();
        var script = _script.Text.Trim();

        if (host.Length == 0) { Log("Enter a host name."); return; }

        var agentExe = Path.Combine(folder, "rdpeek-agent.exe");
        if (!File.Exists(agentExe)) { Log($"rdpeek-agent.exe not found in: {folder}"); return; }
        if (!File.Exists(script)) { Log($"Install script not found: {script}"); return; }

        string uncScript, uncAgent, uncWorkDir;
        try
        {
            uncScript = ToTsClientPath(script);
            uncAgent = ToTsClientPath(agentExe);
            uncWorkDir = ToTsClientPath(folder);
        }
        catch (ArgumentException ex) { Log("Error: " + ex.Message); return; }

        // The alternate shell for the session: run the installer over the redirected
        // drive, then exit (which logs the session off). -WaitSeconds rides out the
        // brief window where \\tsclient may not be mounted yet at logon; -NoStart
        // skips starting a serve that would die with this throwaway session anyway.
        var startProgram = BuildStartProgram(uncScript, uncAgent);

        try
        {
            dynamic rdp = _rdp.Control;
            rdp.Server = host;
            if (user.Length > 0) rdp.UserName = user;
            rdp.DesktopWidth = Math.Max(_rdp.Width, 1024);
            rdp.DesktopHeight = Math.Max(_rdp.Height, 720);

            dynamic adv = rdp.AdvancedSettings2;
            adv.RedirectDrives = true;                 // expose the client drive as \\tsclient
            TrySet("EnableCredSspSupport", () => adv.EnableCredSspSupport = true);
            TrySet("AuthenticationLevel", () => adv.AuthenticationLevel = 2); // connect + warn

            dynamic sec = rdp.SecuredSettings2;
            sec.StartProgram = startProgram;
            sec.WorkDir = uncWorkDir;
            // Forward Windows-key combinations to the session, so the Win+R fallback can reach it.
            TrySet("KeyboardHookMode", () => sec.KeyboardHookMode = 1);

            // Load the RDPeek plugin (via the shim) so we can *detect* the agent's DVC check-in — the
            // primary success signal. Best-effort: if the shim isn't beside us, detection falls back
            // to tailing the plugin log, and failing that, the Win+R provisioning fallback still runs.
            var shim = FindShim();
            if (shim is not null) { TrySet("PluginDlls", () => adv.PluginDlls = shim); Log($"  detection plugin: {shim}"); }

            _startProgram = startProgram;
            _watch = new AgentWatch();   // start the broker pipe + log baseline before connecting

            Log($"Connecting to {host} as {(user.Length > 0 ? user : "(you'll be prompted)")} …");
            Log($"  redirecting the client drive and running:");
            Log($"    {startProgram}");

            _lastState = -1;
            _reachedConnected = false;
            _agentDetected = false;
            rdp.Connect();

            _poll.Start();
            _provision.Enabled = false;
            _disconnect.Enabled = true;
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
            _poll.Stop();
            _provision.Enabled = true;
            _disconnect.Enabled = false;
        }
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        int state;
        try { state = (int)(short)Script(_rdp.Control).Connected; }
        catch { return; }

        if (state == _lastState) return;
        _lastState = state;

        switch (state)
        {
            case 2:
                Log("State: connecting…");
                break;
            case 1:
                _reachedConnected = true;
                Log("State: connected — AlternateShell is running the installer; watching for the agent to check in…");
                _ = DetectAndMaybeFallbackAsync();
                break;
            case 0:
                _poll.Stop();
                _provision.Enabled = true;
                _disconnect.Enabled = false;
                if (_reachedConnected && _agentDetected)
                    Log("Done. Agent detected — it will auto-start on every future RDP connection to this host.");
                else if (_reachedConnected)
                    Log("Session ended, but the agent was not detected — verify provisioning on the host (or re-run).");
                else
                    Log("Disconnected before logon completed — check the host name, credentials, and that RDP is enabled on the target.");
                break;
        }
    }

    private void Disconnect(string why)
    {
        try { Script(_rdp.Control).Disconnect(); } catch { /* already down */ }
        _poll.Stop();
        _provision.Enabled = true;
        _disconnect.Enabled = false;
        Log(why);
    }

    /// <summary>AlternateShell is primary; watch for the agent's check-in (broker report OR plugin
    /// log — whichever wins). If none arrives, inject Win+R + the same StartProgram into the session,
    /// then watch once more. Mirrors the headless probe's strategy.</summary>
    private async Task DetectAndMaybeFallbackAsync()
    {
        if (_watch is null) return;

        var signal = await _watch.WaitAsync(TimeSpan.FromSeconds(20));
        if (signal is not null) { _agentDetected = true; Log($"Agent detected — {signal}."); return; }

        if (string.IsNullOrEmpty(_startProgram)) { Log("No check-in and no command to fall back to."); return; }
        Log("No agent check-in — AlternateShell may have been ignored. Trying the Win+R fallback…");
        await RunFallbackAsync(_startProgram!);

        var after = await _watch.WaitAsync(TimeSpan.FromSeconds(20));
        if (after is not null) { _agentDetected = true; Log($"Agent detected after fallback — {after}."); }
        else Log("Agent still not detected after the fallback.");
    }

    /// <summary>Resolve the control's input child window (UI thread) and post Win+R + the command to
    /// it off-thread — window-targeted, so it reaches the session without stealing focus and never
    /// leaks to the local desktop.</summary>
    private async Task RunFallbackAsync(string command)
    {
        Log($"Fallback: Win+R → {command}");
        var target = (IntPtr)Invoke(() => { Activate(); return SessionKeys.FindInputWindow(_rdp.Handle); });
        await Task.Delay(1200);                       // let the session desktop settle
        await Task.Run(() => SessionKeys.RunViaWinR(target, command));
    }

    /// <summary>Locate rdpeek-vc-shim.dll: beside the exe (a published bundle), else the VcShim build
    /// output in a repo checkout. Null if not found (detection then relies on the plugin log only).</summary>
    private static string? FindShim()
    {
        var here = Path.Combine(AppContext.BaseDirectory, "rdpeek-vc-shim.dll");
        if (File.Exists(here)) return here;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "src", "Rdpeek.VcShim", "bin", "rdpeek-vc-shim.dll");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // ---- helpers -----------------------------------------------------------

    private void TrySet(string name, Action set)
    {
        try { set(); }
        catch (Exception ex) { Log($"  (optional setting '{name}' not applied: {ex.Message})"); }
    }

    private static dynamic Script(object ocx) => ocx;

    /// <summary>Map a local drive-letter path to its in-session redirected form:
    /// <c>C:\dir\file</c> -&gt; <c>\\tsclient\C\dir\file</c>.</summary>
    internal static string ToTsClientPath(string localPath)
    {
        var full = Path.GetFullPath(localPath);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
            throw new ArgumentException(
                $"Expected a local drive-letter path (drive redirection can't map '{localPath}').");

        var drive = char.ToUpperInvariant(root[0]);
        var rest = full[root.Length..].TrimStart('\\');
        return $@"\\tsclient\{drive}\{rest}";
    }

    /// <summary>The session "start program": run the installer over the redirected drive, then
    /// exit (logging the session off). Shared by the installer form and the headless probe.</summary>
    internal static string BuildStartProgram(string uncScript, string uncAgent) =>
        "powershell.exe -NoProfile -ExecutionPolicy Bypass " +
        $"-File \"{uncScript}\" -AgentPath \"{uncAgent}\" -WaitSeconds 45 -NoStart";

    private (string agentFolder, string script) GuessDefaults()
    {
        // Alongside the exe first (a published bundle), then walking up to a repo
        // checkout's publish/agent + tools.
        var exeDir = AppContext.BaseDirectory;
        var agentHere = Path.Combine(exeDir, "rdpeek-agent.exe");
        var agentFolder = File.Exists(agentHere) ? exeDir : "";
        var script = Path.Combine(exeDir, "install-agent-task.ps1");

        var dir = new DirectoryInfo(exeDir);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (agentFolder.Length == 0)
            {
                var pub = Path.Combine(dir.FullName, "publish", "agent", "rdpeek-agent.exe");
                if (File.Exists(pub)) agentFolder = Path.GetDirectoryName(pub)!;
            }

            if (!File.Exists(script))
            {
                var t = Path.Combine(dir.FullName, "tools", "install-agent-task.ps1");
                if (File.Exists(t)) script = t;
            }
        }

        return (agentFolder, File.Exists(script) ? script : "");
    }

    private void Log(string line)
    {
        if (_log.InvokeRequired) { _log.BeginInvoke(() => Log(line)); return; }
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
    }
}
