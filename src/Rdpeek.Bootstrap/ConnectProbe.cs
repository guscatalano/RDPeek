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
    private bool _reachedConnected;
    private DateTime _deadlineUtc;

    /// <summary>0 = reached "connected", 2 = timed out before connecting, 3 = error.</summary>
    public int ExitCode { get; private set; } = 2;

    public ConnectProbe(string target, int holdSeconds, string? agentFolder = null, string? script = null)
    {
        var parts = target.Split(':', 2);
        _host = parts[0];
        _port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 3389;
        _holdSeconds = holdSeconds;
        _agentFolder = agentFolder;
        _script = script;

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
            // AuthenticationLevel 0 would SKIP TLS and request only standard RDP — use >= 1.
            TrySet(() => adv.NegotiateSecurityLayer = true);
            TrySet(() => adv.AuthenticationLevel = 2);
            TrySet(() => adv.EnableAutoReconnect = false);
            TrySet(() => adv.GrabFocusOnConnect = false);

            ApplyProvisioning(rdp);

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
        }
        else if (state == 0 && _reachedConnected)
        {
            Console.WriteLine("session ended.");
            Close();
        }

        if (DateTime.UtcNow >= _deadlineUtc)
        {
            Console.WriteLine(_reachedConnected ? "hold elapsed." : "connect timed out.");
            if (!_reachedConnected) ExitCode = 2;
            Close();
        }
    }

    private static void TrySet(Action set)
    {
        try { set(); } catch { /* optional setting absent on this control generation */ }
    }
}
