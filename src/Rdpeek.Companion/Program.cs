using System.Text;
using System.Windows.Forms;
using Rdpeek.Companion;

internal static class Program
{
    // WinForms + OLE (clipboard) require a single-threaded apartment. Top-level
    // statements don't emit [STAThread], so declare an explicit entry point.
    [STAThread]
    private static void Main(string[] args)
    {
        // --diag: enumerate RDP windows + show the bootstrap command (no UI).
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

            File.WriteAllText(Path.Combine(Path.GetTempPath(), "rdpeek-companion-diag.txt"), sb.ToString());
            return;
        }

        // --broker-listen [seconds]: host the broker, collect reports, dump to a file.
        int brokerArg = Array.FindIndex(args, a => a.Equals("--broker-listen", StringComparison.OrdinalIgnoreCase));
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
                File.WriteAllText(outPath, sb.ToString());
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
    }
}
