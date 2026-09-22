namespace Rdpeek.Client;

/// <summary>One finding, flattened for a UI: severity as a short string plus its message.</summary>
public sealed record DoctorCheck(string Severity, string Message);

/// <summary>One registered DVC plugin's diagnosis, flattened for binding.</summary>
public sealed record PluginReport(
    string Source, string PluginKey, string Name, string Activation,
    string ModulePath, string Bitness, string Clsid, string Worst,
    IReadOnlyList<DoctorCheck> Checks);

/// <summary>
/// UI-facing wrapper over <see cref="DoctorEngine"/>: runs the same registration scan and
/// activation smoke test the console <c>rdpeek-doctor</c> runs, but returns plain records
/// the companion can bind to a Diagnostics tab. Blocking (registry + CoCreateInstance), so
/// callers run it off the UI thread.
/// </summary>
public static class PluginDoctor
{
    public static IReadOnlyList<PluginReport> Run()
    {
        using var _ = DoctorEngine.ComScope();
        var entries = DoctorEngine.Scan();
        DoctorEngine.Diagnose(entries);

        return entries.Select(e => new PluginReport(
            e.Source,
            e.PluginKey,
            e.NameValue,
            e.Activation == Activation.Unknown ? "" : e.Activation.ToString(),
            e.ModulePath,
            e.ModuleBitness == Bitness.Unknown ? "" : e.ModuleBitness.ToString(),
            e.Clsid ?? "",
            e.Worst.ToString(),
            e.Findings.Select(f => new DoctorCheck(f.Severity.ToString(), f.Message)).ToList()))
            .ToList();
    }
}
