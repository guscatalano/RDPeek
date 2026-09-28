using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rdpeek.Tracing;

/// <summary>
/// The self-describing manifest packaged alongside the .etl. Pure data + JSON (no ETW),
/// so building and round-tripping it is unit-testable without a trace session. This is what
/// tells whoever opens the archive what they are looking at: which side captured it, on what
/// host, for how long, and with which providers.
/// </summary>
public sealed class TraceMetadata
{
    /// <summary>Schema tag so a future reader can detect the format.</summary>
    [JsonPropertyName("schema")] public string Schema { get; set; } = CurrentSchema;

    public const string CurrentSchema = "rdpeek-trace/1";

    /// <summary>"server" or "client".</summary>
    [JsonPropertyName("side")] public string Side { get; set; } = "";

    [JsonPropertyName("hostName")] public string HostName { get; set; } = "";
    [JsonPropertyName("toolVersion")] public string ToolVersion { get; set; } = "";
    [JsonPropertyName("osVersion")] public string OsVersion { get; set; } = "";
    [JsonPropertyName("sessionId")] public int SessionId { get; set; }

    [JsonPropertyName("startUtc")] public DateTime StartUtc { get; set; }
    [JsonPropertyName("stopUtc")] public DateTime StopUtc { get; set; }
    [JsonPropertyName("durationSeconds")] public double DurationSeconds { get; set; }

    [JsonPropertyName("etlFileName")] public string EtlFileName { get; set; } = TracePackager.EtlEntryName;
    [JsonPropertyName("etlSizeBytes")] public long EtlSizeBytes { get; set; }

    [JsonPropertyName("providers")] public List<ProviderInfo> Providers { get; set; } = new();

    /// <summary>Anything worth telling the reader: elevation warnings, a dropped provider, etc.</summary>
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();

    public sealed class ProviderInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("guid")] public string Guid { get; set; } = "";
        [JsonPropertyName("module")] public string Module { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Render this manifest as indented JSON (the <c>metadata.json</c> file).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parse a manifest back (used by tests and by any consumer of a package).</summary>
    public static TraceMetadata? FromJson(string json) =>
        JsonSerializer.Deserialize<TraceMetadata>(json, JsonOptions);

    /// <summary>
    /// Build a manifest from the pieces every capture already has. Pure — no ETW, no I/O
    /// beyond reading the .etl size the caller passes in.
    /// </summary>
    public static TraceMetadata Build(
        TraceSide side,
        IEnumerable<TraceProvider> providers,
        DateTime startUtc,
        DateTime stopUtc,
        string hostName,
        string toolVersion,
        int sessionId,
        long etlSizeBytes,
        string osVersion = "",
        IEnumerable<string>? notes = null)
    {
        var meta = new TraceMetadata
        {
            Side = side == TraceSide.Server ? "server" : "client",
            HostName = hostName ?? "",
            ToolVersion = toolVersion ?? "",
            OsVersion = osVersion ?? "",
            SessionId = sessionId,
            StartUtc = startUtc,
            StopUtc = stopUtc,
            DurationSeconds = Math.Round(Math.Max(0, (stopUtc - startUtc).TotalSeconds), 3),
            EtlSizeBytes = etlSizeBytes,
        };
        foreach (var p in providers)
            meta.Providers.Add(new ProviderInfo { Name = p.Name, Guid = p.Id.ToString(), Module = p.Module });
        if (notes is not null) meta.Notes.AddRange(notes.Where(n => !string.IsNullOrWhiteSpace(n)));
        return meta;
    }
}
