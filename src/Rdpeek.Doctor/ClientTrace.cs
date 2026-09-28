using Rdpeek.Tracing;

namespace Rdpeek.Doctor;

/// <summary>
/// Client-side ETW trace: <c>rdpeek-doctor trace</c>. The console counterpart to
/// <see cref="ClientDvcProbe"/> — instead of counting bytes live, it captures mstsc/msrdc's own RDP
/// providers to an .etl and packages it (.etl + manifest → .zip) for sharing. Uses the same
/// <see cref="TraceRunner"/> as the agent, so client and server packages are interchangeable.
///
/// Any ETW session needs Administrator; an unelevated run says so and exits non-zero.
/// </summary>
internal static class ClientTrace
{
    public static int Run(string[] args)
    {
        int seconds = ParseInt(args, "--seconds", 20);
        string? providers = ParseString(args, "--providers");
        string? outDir = ParseString(args, "--out");

        Console.WriteLine();
        Console.WriteLine("  rdpeek-doctor trace — client-side ETW capture (mstsc/msrdc providers)");
        if (!EtwFileTrace.IsElevated())
        {
            Console.Error.WriteLine("  Needs Administrator — an ETW trace session requires it. Re-run elevated.");
            return 2;
        }

        var opts = new TraceRunOptions
        {
            Seconds = seconds,
            ProvidersSpec = providers,
            OutputDirectory = outDir,
            SessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId,
        };

        Console.WriteLine($"  Duration: {opts.ClampedSeconds}s | Providers: {(string.IsNullOrWhiteSpace(providers) ? "client defaults" : providers)}");
        Console.WriteLine("  Connect or use an RDP session while this runs so there is traffic to capture.");
        Console.WriteLine("  " + new string('-', 68));

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var result = TraceRunner.RunAndPackage(TraceSide.Client, opts, cts.Token, m => Console.WriteLine($"  {m}"));

        Console.WriteLine("  " + new string('-', 68));
        if (!result.Ok)
        {
            Console.Error.WriteLine($"  Trace failed: {result.Note}");
            return 1;
        }

        Console.WriteLine($"  {result.Note}");
        Console.WriteLine($"  Package: {result.PackagePath}  ({result.PackageSize:N0} bytes)");
        return 0;
    }

    private static int ParseInt(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }

    private static string? ParseString(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
