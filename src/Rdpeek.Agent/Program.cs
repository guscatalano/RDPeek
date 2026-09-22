using System.Diagnostics;
using Dvc.Diag.Protocol;
using Google.Protobuf;
using Rdpeek.Agent;

// DVC remote agent. Runs inside an RDP session, opens the diagnostics channel, and
// serves collectors to the client viewer.
//
//   rdpeek-agent selftest   Run the collectors locally and print the snapshot (no DVC).
//   rdpeek-agent serve      Open the DVC channel and serve (requires a live RDP session).
//   rdpeek-agent dvcwatch   Live per-channel traffic table in the console (no DVC).
//   rdpeek-agent serve-tcp  Serve the agent over TCP (for a mock RDP server to bridge to).
//
// selftest exists so the real collectors can be verified anywhere, and so the viewer
// can be developed without a deployed agent.

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "selftest";

switch (command)
{
    case "selftest":
    case "once":
        RunSelfTest();
        return 0;

    case "serve":
        // Opens the DVC channel and serves the collectors. Requires a live RDP
        // session with the RDPeek client plugin listening on the same channel.
        // --file-root <path> (repeatable) confines file PULL; defaults to %TEMP%.
        return ServeLoop.Run(ParseFileRoots(args));

    case "serve-tcp":
        // Serves the same AgentCore over a TCP socket, so a mock RDP server can bridge its
        // diagnostics DVC to it and drive the real agent. `serve-tcp <port>` (default 9999).
        return ServeTcp.Run(ParsePort(args, 9999), ParseFileRoots(args), args.Contains("--fake"));

    case "dvcwatch":
        // Standalone per-DVC traffic monitor — the RDP_DVC_Watcher tool this grew from,
        // now reading whichever source is available. Run it on the session host.
        return RunDvcWatch(args.Contains("--etw"));

    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Use: selftest | serve | serve-tcp | dvcwatch");
        return 64;
}

// Port from the first bare numeric arg or --port <n>, else the default.
static int ParsePort(string[] args, int fallback)
{
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) return p;
        if (i > 0 && int.TryParse(args[i], out var bare) && bare is > 0 and < 65536) return bare;
    }
    return fallback;
}

// File-PULL roots from --file-root <path> (repeatable). Default: the session's %TEMP%, where logs
// and crash dumps usually land — a useful, bounded default for a dev diagnostics agent.
static IReadOnlyList<string> ParseFileRoots(string[] args)
{
    var roots = new List<string>();
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i].Equals("--file-root", StringComparison.OrdinalIgnoreCase))
            roots.Add(args[++i]);
    if (roots.Count == 0) roots.Add(Path.GetTempPath());
    return roots;
}

// Live table of per-channel traffic, refreshed in place until Ctrl+C.
static int RunDvcWatch(bool forceEtw)
{
    Console.WriteLine(forceEtw
        ? "rdpeek-agent: per-DVC traffic via ETW (Ctrl+C to stop)"
        : $"rdpeek-agent: per-DVC traffic via {DvcCounters.SourceName} (Ctrl+C to stop)");
    Console.WriteLine("Counters are server-side: run this inside the RDP session host.");
    Console.WriteLine();

    using var stop = new ManualResetEventSlim(false);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

    // Redraw in place when we own a console; when output is redirected there is no
    // cursor to move, so the table just scrolls.
    int top = 0;
    bool redraw = true;
    try { top = Console.CursorTop; } catch (IOException) { redraw = false; }

    while (!stop.IsSet)
    {
        var sample = forceEtw ? DvcTrafficWatcher.Instance.Snapshot() : DvcCounters.Snapshot();

        var lines = new List<string> { $"{DateTime.Now:HH:mm:ss}   source: {sample.Source}" };
        if (sample.Channels.Count == 0)
        {
            lines.Add(string.IsNullOrEmpty(sample.Note) ? "(no channels)" : sample.Note);
        }
        else
        {
            lines.Add($"  {"CHANNEL",-34} {"SENT",12} {"RECV",12} {"SEND/s",13} {"RECV/s",13} {"RTT",8}");
            foreach (var c in sample.Channels)
                lines.Add($"  {Trim(c.Name, 34),-34} {Bytes(c.BytesSent),12} {Bytes(c.BytesReceived),12} " +
                          $"{Rate(c.SendRateBps),13} {Rate(c.RecvRateBps),13} " +
                          $"{(c.RttMs > 0 ? $"{c.RttMs:0.#} ms" : "-"),8}");
        }

        if (redraw)
        {
            try { Console.SetCursorPosition(0, top); } catch (IOException) { redraw = false; }
        }
        foreach (var line in lines) Console.WriteLine(line.PadRight(100));

        stop.Wait(1000);
    }

    DvcCounters.Stop();
    return 0;
}

static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

static string Bytes(ulong n) =>
    n >= 1024UL * 1024 * 1024 ? $"{n / 1024.0 / 1024 / 1024:0.00} GB" :
    n >= 1024UL * 1024 ? $"{n / 1024.0 / 1024:0.00} MB" :
    n >= 1024UL ? $"{n / 1024.0:0.0} KB" : $"{n} B";

static string Rate(double bytesPerSecond) =>
    double.IsFinite(bytesPerSecond) && bytesPerSecond > 0 ? Bytes((ulong)bytesPerSecond) + "/s" : "-";

static void RunSelfTest()
{
    var formatter = new JsonFormatter(JsonFormatter.Settings.Default.WithIndentation("  "));

    Console.WriteLine("== SysInfo ==");
    var sysinfo = SysInfoCollector.Collect();
    Console.WriteLine(formatter.Format(sysinfo));

    Console.WriteLine();
    Console.WriteLine("== Processes (current session) ==");
    uint session = (uint)Process.GetCurrentProcess().SessionId;
    var procs = ProcessCollector.Collect(allSessions: false, currentSessionId: session);
    Console.WriteLine($"count: {procs.Processes.Count}");
    foreach (var p in procs.Processes.OrderByDescending(p => p.WorkingSet).Take(8))
        Console.WriteLine($"  {p.Pid,6}  {p.WorkingSet / 1024 / 1024,5} MB  {p.ImageName,-28} {p.UserName}");

    Console.WriteLine();
    Console.WriteLine("== Network (listening TCP) ==");
    var net = NetCollector.Collect();
    Console.WriteLine($"count: {net.Entries.Count}");
    foreach (var e in net.Entries.Where(e => e.State == "LISTEN").Take(8))
        Console.WriteLine($"  {e.Local,-22} {e.State,-12} {e.Process} ({e.Pid})");

    Console.WriteLine();
    Console.WriteLine("== Sessions ==");
    foreach (var s in SessionCollector.Collect().Sessions)
        Console.WriteLine($"  {s.SessionId,3}  {s.Station,-14} {s.State,-13} {s.User}  {s.ClientName}");

    Console.WriteLine();
    Console.WriteLine("== Services ==");
    var svcs = ServiceCollector.Collect();
    Console.WriteLine($"count: {svcs.Services.Count} (running: {svcs.Services.Count(s => s.Status == "Running")})");
    foreach (var s in svcs.Services.Where(s => s.Status == "Running").Take(6))
        Console.WriteLine($"  {s.Name,-20} {s.Status,-9} {s.StartType,-9} {s.Display}");

    Console.WriteLine();
    Console.WriteLine("== Perf ==");
    var counters = PerfCollector.Collect().Counters;
    foreach (var c in counters.Where(c => c.Group is "" or "host"))
        Console.WriteLine($"  {c.Name,-20} {c.Value} {c.Unit}");

    Console.WriteLine();
    Console.WriteLine("== Link quality (RemoteFX — session host only) ==");
    var link = counters.Where(c => c.Group is "link" or "graphics").ToList();
    if (link.Count == 0)
        Console.WriteLine("  No instances. Expected unless this machine is hosting the RDP session.");
    foreach (var c in link)
        Console.WriteLine($"  [{c.Group,-8}] {Trim(c.Name, 52),-52} {c.Value} {c.Unit}".TrimEnd());

    Console.WriteLine();
    Console.WriteLine($"== DVC traffic (source: {DvcCounters.SourceName}) ==");
    var dvc = DvcCounters.Snapshot();
    if (dvc.Channels.Count == 0)
        Console.WriteLine($"  {dvc.Note}");
    foreach (var c in dvc.Channels)
        Console.WriteLine($"  {Trim(c.Name, 34),-34} sent {Bytes(c.BytesSent),10}  recv {Bytes(c.BytesReceived),10}  " +
                          $"{Rate(c.SendRateBps)} up / {Rate(c.RecvRateBps)} down");

    Console.WriteLine();
    Console.WriteLine("== System detail ==");
    var sd = SystemDetailCollector.Collect();
    Console.WriteLine($"  BuildLabEx: {sd.BuildLabEx}");
    Console.WriteLine($"  Edition   : {sd.EditionId}  {sd.DisplayVersion}   installed {sd.InstallDate}   up {sd.UptimeMs / 1000.0 / 86400.0:0.0} d");
    Console.WriteLine($"  Updates   : {sd.Hotfixes.Count}   GPUs: {sd.Gpus.Count}   PnP devices: {sd.Devices.Count}" +
                      $"   (problem: {sd.Devices.Count(d => d.Problem.Length > 0)})");
    foreach (var g in sd.Gpus)
        Console.WriteLine($"    GPU  {Trim(g.Name, 34),-34} drv {g.DriverVersion} ({g.DriverDate})");
    foreach (var d in sd.Devices.Where(d => d.Problem.Length > 0).Take(6))
        Console.WriteLine($"    !    {Trim(d.Name, 40),-40} {d.Problem}");
    foreach (var n in sd.Notes)
        Console.WriteLine($"    note: {n}");
}
