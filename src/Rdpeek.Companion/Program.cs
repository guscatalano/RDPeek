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

ApplicationConfiguration.Initialize();
Application.Run(new MainForm());
