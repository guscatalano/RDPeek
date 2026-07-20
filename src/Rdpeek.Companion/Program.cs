using System.Text;
using Rdpeek.Companion;

// RDPeek companion — multi-connection aware helper that lists open RDP windows and
// one-click bootstraps the agent into a chosen session (copy from \\tsclient to the
// remote %TEMP%, then serve). No RDP-control hosting; drives mstsc from outside.
//
//   (no args)   launch the UI
//   --diag      print detected RDP windows + the command that would be sent (no UI)

if (args.Any(a => a.Equals("--diag", StringComparison.OrdinalIgnoreCase)))
{
    var sb = new StringBuilder();
    var windows = RdpWindows.Enumerate();
    sb.AppendLine($"Detected RDP windows: {windows.Count}");
    foreach (var w in windows)
        sb.AppendLine($"  host='{w.Host}'  pid={w.Pid}  hwnd=0x{w.Hwnd:X}  title='{w.Title}'");
    sb.AppendLine();
    sb.AppendLine("Sample bootstrap command for C:\\Tools\\rdpeek-agent.exe:");
    sb.AppendLine("  " + InputBootstrap.BuildCommand(@"C:\Tools\rdpeek-agent.exe"));

    var outPath = Path.Combine(Path.GetTempPath(), "rdpeek-companion-diag.txt");
    File.WriteAllText(outPath, sb.ToString());
    Console.WriteLine($"wrote {outPath}");
    return;
}

// Broker self-test: host the broker, collect reports for N seconds, dump to a file.
var brokerArg = Array.FindIndex(args, a => a.Equals("--broker-listen", StringComparison.OrdinalIgnoreCase));
if (brokerArg >= 0)
{
    int seconds = (brokerArg + 1 < args.Length && int.TryParse(args[brokerArg + 1], out int s)) ? s : 6;
    var outPath = Path.Combine(Path.GetTempPath(), "rdpeek-broker-diag.txt");
    var sb = new StringBuilder();
    try
    {
        using var broker = new BrokerServer();
        broker.Start();
        sb.AppendLine($"broker started, listening {seconds}s on pipe '{Rdpeek.Client.Broker.PipeName}'");
        File.WriteAllText(outPath, sb.ToString()); // marker so we know it got this far
        Thread.Sleep(seconds * 1000);

        var states = broker.Snapshot();
        sb.AppendLine($"Broker received {states.Count} live state(s):");
        foreach (var st in states)
            sb.AppendLine($"  pid={st.Pid} seq={st.Seq} status={st.Status} host='{st.Host}'");
    }
    catch (Exception ex)
    {
        sb.AppendLine("ERROR: " + ex);
    }
    File.WriteAllText(outPath, sb.ToString());
    return;
}

ApplicationConfiguration.Initialize();
Application.Run(new MainForm());
