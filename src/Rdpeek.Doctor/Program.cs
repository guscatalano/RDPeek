using Rdpeek.Doctor;

// Standalone DVC plugin registration diagnostician. No RDP session required.
// Exit code: 0 = no failures, 1 = at least one FAIL finding.
//
//   rdpeek-doctor                          Registration check (default).
//   rdpeek-doctor dvcprobe [--discover]    Opt-in client-side DVC traffic probe over
//                                          mstsc's ETW providers. Needs Administrator.
//     --seconds N                          Stop after N seconds (default: Ctrl+C).

if (args.Length > 0 && args[0].Equals("dvcprobe", StringComparison.OrdinalIgnoreCase))
{
    int seconds = 0;
    int at = Array.FindIndex(args, a => a.Equals("--seconds", StringComparison.OrdinalIgnoreCase));
    if (at >= 0 && at + 1 < args.Length) int.TryParse(args[at + 1], out seconds);

    return ClientDvcProbe.Run(
        discover: args.Contains("--discover", StringComparer.OrdinalIgnoreCase),
        seconds: seconds);
}

NativeMethods.CoInitializeEx(IntPtr.Zero, NativeMethods.COINIT_MULTITHREADED);
try
{
    Console.WriteLine();
    Console.WriteLine("  DVC Doctor — RDP dynamic virtual channel plugin registration check");
    Console.WriteLine($"  Host: {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")} Windows | " +
                      $"Doctor process: {(Environment.Is64BitProcess ? "x64" : "x86")}");
    Console.WriteLine("  " + new string('-', 68));

    var entries = DoctorEngine.Scan();
    if (entries.Count == 0)
    {
        Warn("  No DVC plugins registered under Terminal Server Client\\Default\\AddIns.");
        Console.WriteLine("  (Nothing to diagnose — this is normal on a clean machine.)");
        return 0;
    }

    DoctorEngine.Diagnose(entries);

    foreach (var e in entries)
    {
        Console.WriteLine();
        WriteSeverityTag(e.Worst);
        Console.WriteLine($" {e.PluginKey}   [{e.Source}]");
        Console.WriteLine($"       Name      : {e.NameValue}");
        if (e.Activation != Activation.Unknown)
            Console.WriteLine($"       Activation: {e.Activation}");
        if (!string.IsNullOrEmpty(e.ModulePath))
            Console.WriteLine($"       Module    : {e.ModulePath}" +
                              (e.ModuleBitness != Bitness.Unknown ? $" ({e.ModuleBitness})" : ""));

        foreach (var f in e.Findings)
        {
            Console.Write("         ");
            WriteSeverityTag(f.Severity);
            Console.WriteLine($" {f.Message}");
        }
    }

    // Summary
    int fails = entries.Count(e => e.Worst == Severity.Fail);
    int warns = entries.Count(e => e.Worst == Severity.Warn);
    int ok = entries.Count - fails - warns;

    Console.WriteLine();
    Console.WriteLine("  " + new string('-', 68));
    Console.WriteLine($"  {entries.Count} plugin(s): {ok} ok, {warns} warning(s), {fails} failure(s).");
    Console.WriteLine();

    return fails > 0 ? 1 : 0;
}
finally
{
    NativeMethods.CoUninitialize();
}

static void WriteSeverityTag(Severity s)
{
    var (text, color) = s switch
    {
        Severity.Pass => ("[PASS]", ConsoleColor.Green),
        Severity.Info => ("[INFO]", ConsoleColor.Gray),
        Severity.Warn => ("[WARN]", ConsoleColor.Yellow),
        Severity.Fail => ("[FAIL]", ConsoleColor.Red),
        _ => ("[????]", ConsoleColor.Gray),
    };
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.Write(text);
    Console.ForegroundColor = prev;
}

static void Warn(string msg)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine(msg);
    Console.ForegroundColor = prev;
}
