using Rdpeek.Tracing;
using Xunit;

namespace Tracing.Tests;

/// <summary>The manifest is pure data + JSON, so build/serialize/parse round-trips are pinned here.</summary>
public class TraceMetadataTests
{
    [Fact]
    public void Build_PopulatesFieldsAndDuration()
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var stop = start.AddSeconds(30);
        var providers = TraceProviders.ServerDefaults;

        var meta = TraceMetadata.Build(
            TraceSide.Server, providers, start, stop,
            hostName: "HOST01", toolVersion: "1.2.3", sessionId: 4, etlSizeBytes: 2048,
            osVersion: "Windows", notes: new[] { "hello", "  ", "" });

        Assert.Equal("server", meta.Side);
        Assert.Equal("HOST01", meta.HostName);
        Assert.Equal("1.2.3", meta.ToolVersion);
        Assert.Equal(4, meta.SessionId);
        Assert.Equal(2048, meta.EtlSizeBytes);
        Assert.Equal(30, meta.DurationSeconds);
        Assert.Equal(providers.Count, meta.Providers.Count);
        Assert.Equal(TraceMetadata.CurrentSchema, meta.Schema);
        // Blank notes are dropped.
        Assert.Equal(new[] { "hello" }, meta.Notes);
    }

    [Fact]
    public void ClientSide_SerializesAsClient()
    {
        var meta = TraceMetadata.Build(TraceSide.Client, TraceProviders.ClientDefaults,
            DateTime.UtcNow, DateTime.UtcNow, "h", "v", 0, 0);
        Assert.Equal("client", meta.Side);
    }

    [Fact]
    public void JsonRoundTrips()
    {
        var start = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var meta = TraceMetadata.Build(
            TraceSide.Server, TraceProviders.ServerDefaults, start, start.AddSeconds(15),
            "HOSTX", "0.9.0", 7, 999, "OS", new[] { "note-1" });

        string json = meta.ToJson();
        Assert.Contains("\"schema\"", json);
        Assert.Contains("rdpeek-trace/1", json);

        var back = TraceMetadata.FromJson(json);
        Assert.NotNull(back);
        Assert.Equal(meta.Side, back!.Side);
        Assert.Equal(meta.HostName, back.HostName);
        Assert.Equal(meta.ToolVersion, back.ToolVersion);
        Assert.Equal(meta.SessionId, back.SessionId);
        Assert.Equal(meta.EtlSizeBytes, back.EtlSizeBytes);
        Assert.Equal(meta.DurationSeconds, back.DurationSeconds);
        Assert.Equal(meta.Providers.Count, back.Providers.Count);
        Assert.Equal(meta.Notes, back.Notes);
    }
}
