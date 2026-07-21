using System.Diagnostics;
using Dvc.Diag.Protocol;
using Google.Protobuf;
using Rdpeek.Agent;

// DVC remote agent. Runs inside an RDP session, opens the diagnostics channel, and
// serves collectors to the client viewer.
//
//   rdpeek-agent selftest   Run the collectors locally and print the snapshot (no DVC).
//   rdpeek-agent serve      Open the DVC channel and serve (requires a live RDP session).
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
        return ServeLoop.Run();

    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Use: selftest | serve");
        return 64;
}

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
    foreach (var c in PerfCollector.Collect().Counters)
        Console.WriteLine($"  {c.Name,-20} {c.Value} {c.Unit}");
}
