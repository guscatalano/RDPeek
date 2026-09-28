using Microsoft.Diagnostics.Tracing.Session;

namespace Rdpeek.Tracing;

/// <summary>Outcome of a single ETW file capture.</summary>
public readonly record struct EtwCaptureResult(bool Ok, string EtlPath, string Note, IReadOnlyList<TraceProvider> Enabled)
{
    public static EtwCaptureResult Fail(string note) => new(false, "", note, Array.Empty<TraceProvider>());
}

/// <summary>
/// A thin wrapper over <see cref="TraceEventSession"/> for <b>file-mode</b> (.etl) capture. This is
/// the one place that touches ETW; everything else in this library is pure so it can be tested
/// without a trace session. Both the server agent and the client Doctor capture through here — only
/// the provider set and the session name differ.
///
/// Any real-time or file ETW session needs Administrator; callers should gate on
/// <see cref="IsElevated"/> and fail cleanly rather than producing an empty .etl.
/// </summary>
public static class EtwFileTrace
{
    /// <summary>True when the current process can create a kernel/ETW session (Administrator).</summary>
    public static bool IsElevated() => TraceEventSession.IsElevated() == true;

    /// <summary>
    /// Capture the given providers to <paramref name="etlPath"/> for <paramref name="duration"/> (or
    /// until <paramref name="ct"/> fires). Never throws for expected conditions — returns
    /// <see cref="EtwCaptureResult.Ok"/> = false with a reason. A provider that fails to enable is
    /// recorded and skipped; the capture proceeds with the rest.
    /// </summary>
    public static EtwCaptureResult Capture(
        string sessionName,
        IReadOnlyList<TraceProvider> providers,
        string etlPath,
        TimeSpan duration,
        CancellationToken ct = default,
        Action<string>? log = null)
    {
        if (!IsElevated())
            return EtwCaptureResult.Fail("ETW capture needs Administrator — start this elevated so it can open a trace session.");
        if (providers is null || providers.Count == 0)
            return EtwCaptureResult.Fail("No providers to enable.");

        var dir = Path.GetDirectoryName(Path.GetFullPath(etlPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var enabled = new List<TraceProvider>();
        try
        {
            // File-mode session: the OS writes events to the .etl directly. A same-named session left
            // by a crashed run is restarted, not duplicated (default Create behavior). StopOnDispose
            // flushes and closes the file when we leave the using block.
            using var session = new TraceEventSession(sessionName, etlPath) { StopOnDispose = true };

            foreach (var p in providers)
            {
                try
                {
                    session.EnableProvider(p.Id);
                    enabled.Add(p);
                    log?.Invoke($"[on ] {p.Name}");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[skip] {p.Name}: {ex.Message}");
                }
            }

            if (enabled.Count == 0)
                return EtwCaptureResult.Fail("None of the requested providers could be enabled.");

            // Wait out the capture window; a cancel ends it early but still yields a valid .etl.
            log?.Invoke($"capturing for {duration.TotalSeconds:0.#}s …");
            try { ct.WaitHandle.WaitOne(duration); }
            catch (OperationCanceledException) { /* treated as an early, clean stop */ }

            // Dispose (end of using) stops the session and flushes the .etl.
        }
        catch (Exception ex)
        {
            return EtwCaptureResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }

        if (!File.Exists(etlPath))
            return EtwCaptureResult.Fail("Session stopped but no .etl was produced.");

        return new EtwCaptureResult(true, etlPath, "", enabled);
    }
}
