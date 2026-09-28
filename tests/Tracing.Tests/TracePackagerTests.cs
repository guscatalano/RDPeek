using System.IO.Compression;
using System.Text;
using Rdpeek.Tracing;
using Xunit;

namespace Tracing.Tests;

/// <summary>
/// Packaging is pure I/O over a fake .etl, so the archive shape is pinned without a trace session:
/// the .zip must carry the .etl (renamed) and a parseable manifest whose size matches the payload.
/// </summary>
public class TracePackagerTests : IDisposable
{
    private readonly string _dir;

    public TracePackagerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rdpeek-trace-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteFakeEtl(byte[] content)
    {
        string path = Path.Combine(_dir, "capture.etl");
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void CreatePackage_WritesEtlAndMetadata()
    {
        var etlBytes = Encoding.ASCII.GetBytes("not-a-real-etl-but-good-enough");
        string etl = WriteFakeEtl(etlBytes);
        var meta = TraceMetadata.Build(TraceSide.Server, TraceProviders.ServerDefaults,
            DateTime.UtcNow, DateTime.UtcNow.AddSeconds(5), "HOST", "1.0.0", 2, etlSizeBytes: 0);

        string zip = Path.Combine(_dir, "out.zip");
        string result = TracePackager.CreatePackage(etl, meta, zip);

        Assert.True(File.Exists(result));
        using var archive = ZipFile.OpenRead(result);

        var etlEntry = archive.GetEntry(TracePackager.EtlEntryName);
        var metaEntry = archive.GetEntry(TracePackager.MetadataEntryName);
        Assert.NotNull(etlEntry);
        Assert.NotNull(metaEntry);

        using var es = etlEntry!.Open();
        using var ms = new MemoryStream();
        es.CopyTo(ms);
        Assert.Equal(etlBytes, ms.ToArray());

        using var mr = new StreamReader(metaEntry!.Open());
        var parsed = TraceMetadata.FromJson(mr.ReadToEnd());
        Assert.NotNull(parsed);
        Assert.Equal("server", parsed!.Side);
        // The manifest's etl size is refreshed from the real file, even though we passed 0.
        Assert.Equal(etlBytes.Length, parsed.EtlSizeBytes);
        Assert.Equal(TracePackager.EtlEntryName, parsed.EtlFileName);
    }

    [Fact]
    public void CreatePackage_OverwritesExisting()
    {
        string etl = WriteFakeEtl(new byte[] { 1, 2, 3 });
        var meta = TraceMetadata.Build(TraceSide.Client, TraceProviders.ClientDefaults,
            DateTime.UtcNow, DateTime.UtcNow, "H", "v", 0, 0);
        string zip = Path.Combine(_dir, "dup.zip");

        TracePackager.CreatePackage(etl, meta, zip);
        // Second call must not throw on an existing destination.
        string result = TracePackager.CreatePackage(etl, meta, zip);
        Assert.True(File.Exists(result));
    }

    [Fact]
    public void CreatePackage_MissingEtl_Throws()
    {
        var meta = TraceMetadata.Build(TraceSide.Server, TraceProviders.ServerDefaults,
            DateTime.UtcNow, DateTime.UtcNow, "H", "v", 0, 0);
        Assert.Throws<FileNotFoundException>(() =>
            TracePackager.CreatePackage(Path.Combine(_dir, "nope.etl"), meta, Path.Combine(_dir, "x.zip")));
    }

    [Theory]
    [InlineData(TraceSide.Server, "server")]
    [InlineData(TraceSide.Client, "client")]
    public void DefaultPackageFileName_HasSideHostAndZipExtension(TraceSide side, string tag)
    {
        var when = new DateTime(2026, 3, 4, 5, 6, 7);
        string name = TracePackager.DefaultPackageFileName(side, "My Host!", when);
        Assert.StartsWith($"rdpeek-trace-{tag}-", name);
        Assert.EndsWith(".zip", name);
        Assert.Contains("20260304-050607", name);
        // Unsafe filename characters are sanitized out.
        Assert.DoesNotContain(" ", name);
        Assert.DoesNotContain("!", name);
    }

    [Fact]
    public void Sanitize_KeepsAlnumAndDash()
    {
        Assert.Equal("a-b-1", TracePackager.Sanitize("a/b 1"));
    }
}
