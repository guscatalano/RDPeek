using System.Windows.Forms;

namespace Rdpeek.Bootstrap;

/// <summary>
/// Connects the hosted RDP control to a host and holds the session open. With no
/// <see cref="_agentFolder"/>/<see cref="_script"/> it is a plain connectivity probe; with
/// them it applies the installer's real provisioning config (drive redirection + the
/// <c>StartProgram</c> that runs install-agent-task.ps1 over <c>\\tsclient</c>), so a server
/// can observe exactly what the installer transmits — the requested <c>rdpdr</c> channel and
/// the Client Info AlternateShell. NLA is disabled and the certificate is not checked,
/// matching a TLS-only test server.
/// </summary>
internal sealed class ConnectProbe : Form
{
    private readonly AxRdpClient _rdp = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 500 };
    private readonly string _host;
    private readonly int _port;
    private readonly int _holdSeconds;
    private readonly string? _agentFolder;
    private readonly string? _script;
    private readonly string? _pluginDll;
    private readonly int _detectSeconds;
    private readonly bool _forceFallback;
    private readonly string? _fallbackCommand;
    private AgentWatch? _watch;
    private bool _reachedConnected;
    private bool _agentDetected;
    private DateTime _deadlineUtc;

    /// <summary>0 = reached "connected", 2 = timed out before connecting, 3 = error.</summary>
    public int ExitCode { get; private set; } = 2;

    public ConnectProbe(string target, int holdSeconds, string? agentFolder = null, string? script = null,
        string? pluginDll = null, int detectSeconds = 0, bool forceFallback = false, string? fallbackCommand = null)
    {
        var parts = target.Split(':', 2);
        _host = parts[0];
        _port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 3389;
        _holdSeconds = holdSeconds;
        _agentFolder = agentFolder;
        _script = script;
        _pluginDll = pluginDll;
        // How long to wait for the agent before the fallback; default to most of the hold window.
        _detectSeconds = detectSeconds > 0 ? detectSeconds : Math.Max(1, holdSeconds - 4);
        _forceFallback = forceFallback;
        _fallbackCommand = fallbackCommand;

        Text = $"RDPeek connect probe — {target}";
        Width = 1024;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;

        Controls.Add(_rdp);
        Shown += OnShown;
        _poll.Tick += OnPoll;
    }

    private void OnShown(object? sender, EventArgs e)
    {
        try
        {
            dynamic rdp = _rdp.Control;
            rdp.Server = _host;
            TrySet(() => rdp.UserName = "rdpeek");   // the mock ignores creds, but the control wants a user
            TrySet(() => rdp.DesktopWidth = 1024);
            TrySet(() => rdp.DesktopHeight = 720);

            dynamic adv = rdp.AdvancedSettings2;
            adv.RDPPort = _port;
            TrySet(() => adv.ClearTextPassword = "rdpeek");
            // Offer the negotiation-based security layer so the SSL bit is present in the
            // X.224 request; a TLS-only server (like the mock) then selects SSL. NOTE:
            // AuthenticationLevel 0 does NOT just skip the cert check — it drops to Standard RDP
            // security and requests "Rdp" (no TLS bit), which a TLS-only server rejects
            // (SSL_REQUIRED_BY_SERVER). Verified 2026-09-20. Keep >= 1; trust/pin the cert instead.
            TrySet(() => adv.NegotiateSecurityLayer = true);
            TrySet(() => adv.AuthenticationLevel = 2);
            TrySet(() => adv.EnableAutoReconnect = false);
            TrySet(() => adv.GrabFocusOnConnect = false);
            // KeyboardHookMode 1 = send Windows-key combinations to the remote computer, so the
            // Win+R fallback actually reaches the session (default 2 = fullscreen only).
            TrySet(() => rdp.SecuredSettings2.KeyboardHookMode = 1);

            ApplyPluginDll(adv);
            ApplyProvisioning(rdp);

            // Start watching for the agent's DVC check-in before connecting, so the broker pipe is
            // already listening and the log baseline is taken now (stale lines won't count).
            _watch = new AgentWatch();

            Console.WriteLine($"connecting to {_host}:{_port} …");
            rdp.Connect();
            _deadlineUtc = DateTime.UtcNow.AddSeconds(_holdSeconds + 30); // connect timeout window
            _poll.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("connect error: " + ex.Message);
            ExitCode = 3;
            Close();
        }
    }

    /// <summary>Loads a DVC plugin DLL (one exporting VirtualChannelGetInstance) into the hosted
    /// control via its <c>PluginDlls</c> setting — the only plugin-load path this control honours,
    /// since it ignores the client COM AddIns that mstsc.exe activates. Point it at
    /// rdpeek-vc-shim.dll to load the RDPeek plugin headlessly.</summary>
    private void ApplyPluginDll(dynamic adv)
    {
        if (string.IsNullOrEmpty(_pluginDll)) return;
        var full = Path.GetFullPath(_pluginDll!);
        if (!File.Exists(full))
        {
            Console.Error.WriteLine($"--plugin-dll not found: {full}");
            return;
        }
        // PluginDlls is a semicolon-separated list of DVC plugin DLL paths, applied before Connect().
        TrySet(() => adv.PluginDlls = full);
        Console.WriteLine($"PluginDlls: {full}");
    }

    /// <summary>When an agent folder + script are supplied, drive the same redirection and
    /// StartProgram the installer sends, so the server sees the real provisioning request.</summary>
    private void ApplyProvisioning(dynamic rdp)
    {
        if (string.IsNullOrEmpty(_agentFolder) || string.IsNullOrEmpty(_script)) return;

        var agentExe = Path.Combine(_agentFolder!, "rdpeek-agent.exe");
        var startProgram = BootstrapForm.BuildStartProgram(
            BootstrapForm.ToTsClientPath(_script!), BootstrapForm.ToTsClientPath(agentExe));

        dynamic adv = rdp.AdvancedSettings2;
        TrySet(() => adv.RedirectDrives = true);              // requests the rdpdr channel
        dynamic sec = rdp.SecuredSettings2;
        TrySet(() => sec.StartProgram = startProgram);        // -> Client Info AlternateShell
        TrySet(() => sec.WorkDir = BootstrapForm.ToTsClientPath(_agentFolder!));
        Console.WriteLine("provisioning: RedirectDrives=true");
        Console.WriteLine($"provisioning StartProgram: {startProgram}");
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        int state;
        try { state = (short)((dynamic)_rdp.Control).Connected; }
        catch { return; }

        if (state == 1 && !_reachedConnected)
        {
            _reachedConnected = true;
            ExitCode = 0;
            Console.WriteLine("connected — holding the session so the plugin's DVC can connect.");
            _deadlineUtc = DateTime.UtcNow.AddSeconds(_holdSeconds);
            _ = DetectAgentAsync();
        }
        else if (state == 0 && _reachedConnected)
        {
            Console.WriteLine("session ended.");
            Close();
        }

        if (DateTime.UtcNow >= _deadlineUtc)
        {
            if (_reachedConnected)
                Console.WriteLine(_agentDetected ? "hold elapsed (agent detected)." : "hold elapsed (no agent).");
            else
                Console.WriteLine("connect timed out.");
            if (!_reachedConnected) ExitCode = 2;
            Close();
        }
    }

    /// <summary>The provisioning strategy: AlternateShell (already sent) is primary; watch for the
    /// agent's check-in (broker report OR plugin log — whichever wins). If it doesn't arrive in time,
    /// fall back to injecting Win+R + the command into the session, then watch once more.</summary>
    private async Task DetectAgentAsync()
    {
        if (_watch is null) return;

        if (!_forceFallback)
        {
            var signal = await _watch.WaitAsync(TimeSpan.FromSeconds(_detectSeconds));
            if (signal is not null) { _agentDetected = true; Console.WriteLine($"agent detected — {signal}"); return; }
            Console.WriteLine($"no agent after {_detectSeconds}s — AlternateShell may have been ignored; trying Win+R fallback.");
        }
        else
        {
            Console.WriteLine("--force-fallback: skipping detection, going straight to Win+R.");
        }

        var command = FallbackCommand();
        if (command is null)
        {
            Console.WriteLine("fallback unavailable: no --script/--agent-folder or --fallback-command to run.");
            return;
        }

        // The fallback (settle + type + a second detection window) runs past the original hold, so
        // push the auto-close out to cover it.
        _deadlineUtc = DateTime.UtcNow.AddSeconds(Math.Max(4, _detectSeconds) + 10);
        await RunFallbackAsync(command);

        // Give the (now hopefully started) agent a second window to check in.
        var after = await _watch.WaitAsync(TimeSpan.FromSeconds(Math.Max(4, _detectSeconds)));
        if (after is not null) { _agentDetected = true; Console.WriteLine($"agent detected after fallback — {after}"); }
        else Console.WriteLine("agent still not detected after the fallback.");
    }

    /// <summary>The command the fallback types into Run: an explicit override, else the same
    /// StartProgram the installer sends via AlternateShell.</summary>
    private string? FallbackCommand()
    {
        if (!string.IsNullOrEmpty(_fallbackCommand)) return _fallbackCommand;
        if (string.IsNullOrEmpty(_agentFolder) || string.IsNullOrEmpty(_script)) return null;
        var agentExe = Path.Combine(_agentFolder!, "rdpeek-agent.exe");
        return BootstrapForm.BuildStartProgram(
            BootstrapForm.ToTsClientPath(_script!), BootstrapForm.ToTsClientPath(agentExe));
    }

    /// <summary>Resolve the RDP control's input child window (UI thread), then inject the keystrokes
    /// off-thread by posting them to that window — so they reach the session without depending on
    /// foreground focus, and never leak to the local desktop. SessionKeys sleeps between keys, which
    /// must not block the control's message pump, hence the background thread.</summary>
    private async Task RunFallbackAsync(string command)
    {
        Console.WriteLine($"fallback: Win+R → {command}");

        var target = (IntPtr)Invoke(() =>
        {
            var root = _rdp.Handle;
            foreach (var (h, cls, depth) in SessionKeys.Descendants(root))
                Console.WriteLine($"  window {new string(' ', depth)}[{cls}] 0x{h:X}");
            return SessionKeys.FindInputWindow(root);
        });
        Console.WriteLine($"  target input window: 0x{target:X}");

        await Task.Delay(1200);                       // let the session desktop settle
        await Task.Run(() => SessionKeys.RunViaWinR(target, command));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _watch?.Dispose();
    }

    private static void TrySet(Action set)
    {
        try { set(); } catch { /* optional setting absent on this control generation */ }
    }
}
