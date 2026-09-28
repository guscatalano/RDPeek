using Rdpeek.Tracing;

namespace Rdpeek.Agent;

/// <summary>
/// Server-side ETW trace, driven from the console: <c>rdpeek-agent trace</c>. Captures the
/// RDP/DVC server providers on the session host and packages the .etl + a manifest into a shareable
/// .zip. This is the "at will" CLI trigger — the same <see cref="TraceRunner"/> the DVC-triggered
/// path in <see cref="AgentCore"/> uses, so both routes produce identical packages.
///
/// ETW needs Administrator; an unelevated run reports why and exits non-zero rather than writing an
/// empty trace.
/// </summary>
internal static class AgentTrace
{
    public static int Run(string[] args)
    {
        int seconds = ParseInt(args, "--seconds", 20);
        string? providers = ParseString(args, "--providers");
        string? outDir = ParseString(args, "--out");

        Console.WriteLine();
        Console.WriteLine("  rdpeek-agent trace — server-side ETW capture (run inside the RDP session host)");
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

        Console.WriteLine($"  Duration: {opts.ClampedSeconds}s | Providers: {(string.IsNullOrWhiteSpace(providers) ? "server defaults" : providers)}");
        Console.WriteLine("  " + new string('-', 68));

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var result = TraceRunner.RunAndPackage(TraceSide.Server, opts, cts.Token, m => Console.WriteLine($"  {m}"));

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
