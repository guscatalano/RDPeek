using System.Reflection;

namespace Rdpeek.Tracing;

/// <summary>Knobs for one trace run. All optional but <see cref="Side"/>.</summary>
public sealed class TraceRunOptions
{
    /// <summary>How long to capture. Clamped to [<see cref="MinSeconds"/>, <see cref="MaxSeconds"/>].</summary>
    public int Seconds { get; set; } = 20;

    /// <summary>Provider spec (see <see cref="TraceProviders.Parse"/>). Empty => defaults for the side.</summary>
    public string? ProvidersSpec { get; set; }

    /// <summary>Directory to write the .zip into. Null/empty => the system temp directory.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>Explicit output .zip path. Wins over <see cref="OutputDirectory"/> when set.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Session id to record in the manifest (agent uses the RDP session id).</summary>
    public int SessionId { get; set; }

    /// <summary>Extra manifest notes the caller wants to attach.</summary>
    public List<string> Notes { get; } = new();

    public const int MinSeconds = 1;
    public const int MaxSeconds = 600;   // 10 min hard cap so a runaway request can't fill the disk

    public int ClampedSeconds => Math.Clamp(Seconds, MinSeconds, MaxSeconds);
}

/// <summary>Outcome of <see cref="TraceRunner.RunAndPackage"/>.</summary>
public sealed class TraceRunResult
{
    public bool Ok { get; init; }
    /// <summary>Full path to the .zip package (empty on failure).</summary>
    public string PackagePath { get; init; } = "";
    public long PackageSize { get; init; }
    /// <summary>The manifest that was embedded (also handy for a quick UI preview).</summary>
    public TraceMetadata? Metadata { get; init; }
    /// <summary>Human-readable status / failure reason.</summary>
    public string Note { get; init; } = "";

    public static TraceRunResult Fail(string note) => new() { Ok = false, Note = note };
}

/// <summary>
/// The full "start → collect → package" flow shared by every trigger (agent CLI, Doctor CLI, the
/// Companion, and the DVC-triggered agent path). Checks elevation, captures to a temporary .etl,
/// builds the manifest, zips both together, and deletes the temp .etl. Windows/ETW only through
/// <see cref="EtwFileTrace"/>; the rest is pure so this orchestration is easy to reason about.
/// </summary>
public static class TraceRunner
{
    /// <summary>The tool version stamped into the manifest — the entry assembly's version.</summary>
    public static string ToolVersion =>
        (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "?";

    public static TraceRunResult RunAndPackage(
        TraceSide side,
        TraceRunOptions options,
        CancellationToken ct = default,
        Action<string>? log = null)
    {
        options ??= new TraceRunOptions();

        if (!EtwFileTrace.IsElevated())
            return TraceRunResult.Fail("ETW tracing needs Administrator. Re-run elevated — refusing to produce an empty trace.");

        var providers = TraceProviders.Parse(options.ProvidersSpec, side, out var rejected);
        var notes = new List<string>(options.Notes);
        foreach (var r in rejected) notes.Add($"ignored unrecognized provider token: {r}");

        int seconds = options.ClampedSeconds;
        if (seconds != options.Seconds) notes.Add($"duration clamped to {seconds}s (requested {options.Seconds}s).");

        string host = SafeHostName();
        DateTime nowLocal = DateTime.Now;

        // Resolve the output package path.
        string destZip = options.OutputPath is { Length: > 0 } explicitPath
            ? Path.GetFullPath(explicitPath)
            : Path.Combine(
                string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.GetTempPath() : options.OutputDirectory!,
                TracePackager.DefaultPackageFileName(side, host, nowLocal));

        // Capture to a temp .etl we own, then fold it into the package.
        string etlPath = Path.Combine(Path.GetTempPath(), $"rdpeek-trace-{Guid.NewGuid():N}.etl");
        string sessionName = side == TraceSide.Server ? "RDPeek-ServerTrace" : "RDPeek-ClientTrace";

        DateTime startUtc = DateTime.UtcNow;
        var capture = EtwFileTrace.Capture(sessionName, providers, etlPath, TimeSpan.FromSeconds(seconds), ct, log);
        DateTime stopUtc = DateTime.UtcNow;

        if (!capture.Ok)
        {
            TryDelete(etlPath);
            return TraceRunResult.Fail(capture.Note);
        }

        // Note any providers that dropped out so the manifest is honest.
        if (capture.Enabled.Count < providers.Count)
        {
            var enabledIds = capture.Enabled.Select(p => p.Id).ToHashSet();
            foreach (var p in providers.Where(p => !enabledIds.Contains(p.Id)))
                notes.Add($"provider not enabled on this build: {p.Name} ({p.Id})");
        }

        try
        {
            long etlSize = new FileInfo(etlPath).Length;
            var metadata = TraceMetadata.Build(
                side, capture.Enabled, startUtc, stopUtc,
                host, ToolVersion, options.SessionId, etlSize,
                osVersion: Environment.OSVersion.VersionString, notes: notes);

            string package = TracePackager.CreatePackage(etlPath, metadata, destZip);
            long size = new FileInfo(package).Length;
            log?.Invoke($"packaged {size:N0} bytes -> {package}");
            return new TraceRunResult
            {
                Ok = true,
                PackagePath = package,
                PackageSize = size,
                Metadata = metadata,
                Note = $"Captured {metadata.DurationSeconds:0.#}s from {capture.Enabled.Count} provider(s).",
            };
        }
        catch (Exception ex)
        {
            return TraceRunResult.Fail($"packaging failed: {ex.Message}");
        }
        finally
        {
            TryDelete(etlPath);
        }
    }

    private static string SafeHostName()
    {
        try { return Environment.MachineName; } catch { return "host"; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
