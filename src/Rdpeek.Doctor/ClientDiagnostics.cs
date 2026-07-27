using Rdpeek.Client;

namespace Rdpeek.Doctor;

/// <summary>
/// Headless checks for the client half of the link: which RDP windows are open, and
/// what the plugin actually reports over the broker pipe.
///
/// These matter because the plugin lives inside mstsc's COM server, where attaching a
/// debugger is awkward — being able to watch the plugin → broker traffic with no UI in
/// the way is often the fastest way to tell "plugin never loaded" from "plugin loaded
/// but the agent isn't there".
/// </summary>
internal static class ClientDiagnostics
{
    /// <summary>Enumerate the mstsc windows the companion would correlate agents against.</summary>
    public static int ListRdpWindows()
    {
        var windows = RdpWindows.Enumerate();

        Console.WriteLine();
        Console.WriteLine($"  Detected RDP windows: {windows.Count}");
        foreach (var w in windows)
            Console.WriteLine($"    host='{w.Host}'  pid={w.Pid}  hwnd=0x{w.Hwnd:X}  title='{w.Title}'");

        if (windows.Count == 0)
            Console.WriteLine("    (none — connect with mstsc, or the window title didn't parse as a host)");

        Console.WriteLine();
        return windows.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// Host the broker pipe and print what plugin connections report, live. This is the
    /// same server the companion runs, so it must NOT be run while the companion is up —
    /// and not elevated either, or the medium-integrity plugin can't reach it.
    /// </summary>
    public static int ListenToBroker(int seconds)
    {
        if (seconds <= 0) seconds = 6;

        Console.WriteLine();
        Console.WriteLine($"  Listening on pipe '{Broker.PipeName}' for {seconds}s.");
        Console.WriteLine("  Close the RDPeek companion first — only one process can host the pipe.");
        Console.WriteLine("  " + new string('-', 68));

        using var broker = new BrokerServer();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        broker.Changed += () =>
        {
            foreach (var st in broker.Snapshot())
            {
                // Report each connection once per state change, not once per poll.
                string line = $"pid={st.Pid} seq={st.Seq} status={st.Status} host='{st.Host}'";
                if (!seen.Add(line)) continue;

                var payloads = new List<string>();
                if (st.Sysinfo is not null) payloads.Add("sysinfo");
                if (st.Procs is not null) payloads.Add($"procs({st.Procs.Processes.Count})");
                if (st.Net is not null) payloads.Add($"net({st.Net.Entries.Count})");
                if (st.Sessions is not null) payloads.Add($"sessions({st.Sessions.Sessions.Count})");
                if (st.Services is not null) payloads.Add($"services({st.Services.Services.Count})");
                if (st.Perf is not null) payloads.Add("perf");
                if (st.Counters is not null) payloads.Add($"counters({st.Counters.Channels.Count})");

                Console.WriteLine($"  {DateTime.Now:HH:mm:ss}  {line}" +
                                  (payloads.Count > 0 ? "  [" + string.Join(" ", payloads) + "]" : ""));
            }
        };

        broker.Start();
        Thread.Sleep(seconds * 1000);

        var states = broker.Snapshot();
        Console.WriteLine("  " + new string('-', 68));
        Console.WriteLine($"  {states.Count} live connection(s) at the end of the window.");

        if (states.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Nothing reported. Either no RDP session is open, the plugin isn't registered");
            Console.WriteLine("  (run rdpeek-doctor with no arguments), or this process is elevated and the");
            Console.WriteLine("  plugin cannot write to the pipe.");
        }

        Console.WriteLine();
        return states.Count > 0 ? 0 : 1;
    }
}
