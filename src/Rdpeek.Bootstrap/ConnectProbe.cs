using System.Windows.Forms;

namespace Rdpeek.Bootstrap;

/// <summary>
/// Connects the hosted RDP control to a host and holds the session open, so a registered
/// RDPeek client plugin loads and its DVC (dvc::diag::inspector) connects. Used to drive
/// a real connection against the mock RDP server (which opens that channel server-side):
/// success is confirmed out-of-band by the plugin log, this probe just establishes and
/// holds the link. NLA is disabled and the certificate is not checked, matching the mock's
/// TLS-only, no-auth posture.
/// </summary>
internal sealed class ConnectProbe : Form
{
    private readonly AxRdpClient _rdp = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 500 };
    private readonly string _host;
    private readonly int _port;
    private readonly int _holdSeconds;
    private bool _reachedConnected;
    private DateTime _deadlineUtc;

    /// <summary>0 = reached "connected", 2 = timed out before connecting, 3 = error.</summary>
    public int ExitCode { get; private set; } = 2;

    public ConnectProbe(string target, int holdSeconds)
    {
        var parts = target.Split(':', 2);
        _host = parts[0];
        _port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 3389;
        _holdSeconds = holdSeconds;

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

            dynamic adv = rdp.AdvancedSettings2;
            adv.RDPPort = _port;
            TrySet(() => adv.EnableCredSspSupport = false); // mock is TLS-only, no NLA
            TrySet(() => adv.AuthenticationLevel = 0);      // don't fail on the self-signed cert

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
