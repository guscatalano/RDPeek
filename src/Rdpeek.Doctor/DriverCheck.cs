using Rdpeek.Client;

namespace Rdpeek.Doctor;

/// <summary>`rdpeek-doctor drivers` — check this (client) machine's display drivers against RDPeek's
/// known-bad list (fetched from GitHub, embedded fallback). Server-side drivers are checked in the
/// Companion, which has the agent's report. Exit code 1 if any adapter hits a FAIL advisory.</summary>
internal static class DriverCheck
{
    public static int Run()
    {
        Console.WriteLine();
        Console.WriteLine("  Graphics-driver advisory check (client side)");
        Console.WriteLine("  " + new string('-', 68));

        var list = DriverAdvisories.LoadAsync().GetAwaiter().GetResult();
        var gpus = GpuProbe.Local();

        if (gpus.Count == 0)
        {
            Console.WriteLine("  No display adapters found via WMI.");
            Console.WriteLine();
            return 0;
        }

        Console.WriteLine($"  List: schema {list.Schema}, updated {list.Updated}, {list.Entries.Count} entr(y|ies).");
        foreach (var g in gpus)
            Console.WriteLine($"    · {g.Name}   driver {g.DriverVersion}   dated {(string.IsNullOrEmpty(g.DriverDate) ? "?" : g.DriverDate)}");

        var advisories = DriverAdvisories.Evaluate(list, gpus);
        Console.WriteLine();
        if (advisories.Count == 0)
        {
            Console.WriteLine("  [PASS] No known-bad or notably-old client display drivers.");
            Console.WriteLine();
            return 0;
        }

        foreach (var a in advisories)
        {
            var tag = a.Severity switch { "fail" => "FAIL", "warn" => "WARN", _ => "INFO" };
            Console.WriteLine($"  [{tag}] {a.Adapter}");
            Console.WriteLine($"         {a.Message}");
            if (!string.IsNullOrEmpty(a.Link)) Console.WriteLine($"         {a.Link}");
        }
        Console.WriteLine();
        return advisories.Any(a => a.Severity == "fail") ? 1 : 0;
    }
}
