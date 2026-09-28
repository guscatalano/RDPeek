using System.IO.Compression;

namespace Rdpeek.Tracing;

/// <summary>
/// Bundles a captured <c>.etl</c> and its <see cref="TraceMetadata"/> into a single, shareable
/// <c>.zip</c>. Pure I/O over the BCL (System.IO.Compression) — no ETW — so packaging is
/// unit-testable against a fake .etl file without a trace session.
/// </summary>
public static class TracePackager
{
    /// <summary>The .etl's name inside the archive (kept stable so the manifest can name it).</summary>
    public const string EtlEntryName = "trace.etl";

    /// <summary>The manifest's name inside the archive.</summary>
    public const string MetadataEntryName = "metadata.json";

    /// <summary>
    /// A default, filesystem-safe package name: <c>rdpeek-trace-{side}-{host}-{yyyyMMdd-HHmmss}.zip</c>.
    /// </summary>
    public static string DefaultPackageFileName(TraceSide side, string host, DateTime whenLocal)
    {
        string s = side == TraceSide.Server ? "server" : "client";
        string safeHost = Sanitize(string.IsNullOrWhiteSpace(host) ? "host" : host);
        return $"rdpeek-trace-{s}-{safeHost}-{whenLocal:yyyyMMdd-HHmmss}.zip";
    }

    /// <summary>Replace anything that isn't a letter/digit/dash with a dash, for a safe filename fragment.</summary>
    public static string Sanitize(string s) =>
        new(s.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-').ToArray());

    /// <summary>
    /// Create <paramref name="destZipPath"/> containing the .etl (renamed to <see cref="EtlEntryName"/>)
    /// and <paramref name="metadata"/> serialized to <see cref="MetadataEntryName"/>. The metadata's
    /// <see cref="TraceMetadata.EtlSizeBytes"/> is refreshed from the real file so the manifest can't
    /// disagree with the archive. Overwrites an existing destination.
    /// </summary>
    /// <returns>The full path to the written package.</returns>
    public static string CreatePackage(string etlPath, TraceMetadata metadata, string destZipPath)
    {
        if (string.IsNullOrWhiteSpace(etlPath)) throw new ArgumentException("etlPath is required", nameof(etlPath));
        if (!File.Exists(etlPath)) throw new FileNotFoundException("trace .etl not found", etlPath);
        if (metadata is null) throw new ArgumentNullException(nameof(metadata));
        if (string.IsNullOrWhiteSpace(destZipPath)) throw new ArgumentException("destZipPath is required", nameof(destZipPath));

        var destDir = Path.GetDirectoryName(Path.GetFullPath(destZipPath));
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

        // Keep the manifest honest about the payload it ships with.
        metadata.EtlSizeBytes = new FileInfo(etlPath).Length;
        metadata.EtlFileName = EtlEntryName;
        string metadataJson = metadata.ToJson();

        if (File.Exists(destZipPath)) File.Delete(destZipPath);

        using (var zip = ZipFile.Open(destZipPath, ZipArchiveMode.Create))
        {
            // The manifest first, so it reads even from a truncated/partial archive.
            var metaEntry = zip.CreateEntry(MetadataEntryName, CompressionLevel.Optimal);
            using (var w = new StreamWriter(metaEntry.Open()))
                w.Write(metadataJson);

            // Stream the .etl in with a shared read so an in-progress reader can't block us.
            var etlEntry = zip.CreateEntry(EtlEntryName, CompressionLevel.Optimal);
            using var src = new FileStream(etlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = etlEntry.Open();
            src.CopyTo(dst);
        }

        return Path.GetFullPath(destZipPath);
    }
}
