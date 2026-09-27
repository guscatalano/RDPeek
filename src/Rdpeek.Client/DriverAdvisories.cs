using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Rdpeek.Client;

/// <summary>One display adapter to evaluate — the shape both the client-local probe (<see cref="GpuProbe"/>)
/// and the agent's server-side <c>SystemDetail.Gpu</c> reduce to.</summary>
public sealed record GpuDriver(string Name, string DriverVersion, string DriverDate, string Side);

/// <summary>A warning to surface: a matched known-bad driver, or an old-driver note.</summary>
public sealed record DriverAdvisory(string Severity, string Adapter, string Side, string Message, string Link);

/// <summary>
/// Known-bad graphics-driver advisories. The list is a JSON file checked into the repo
/// (<c>data/bad-gfx-drivers.json</c>); at runtime we fetch the raw copy from GitHub (best-effort,
/// cached), and fall back to the copy embedded in this assembly so it always works offline. Pure,
/// transport-free logic so both the Companion and the headless Doctor share it.
/// </summary>
public static class DriverAdvisories
{
    // Raw list on the default branch. Kept in one place so a fork only changes this line.
    public const string ListUrl =
        "https://raw.githubusercontent.com/guscatalano/RDPeek/main/data/bad-gfx-drivers.json";

    private const string EmbeddedName = "bad-gfx-drivers.json";   // LogicalName in the csproj
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    // ── the list model (matches data/bad-gfx-drivers.json) ──────────────────
    public sealed class BadDriverList
    {
        [JsonPropertyName("schema")]          public int Schema { get; set; }
        [JsonPropertyName("updated")]         public string Updated { get; set; } = "";
        [JsonPropertyName("oldDriverMonths")] public int OldDriverMonths { get; set; }
        [JsonPropertyName("entries")]         public List<Entry> Entries { get; set; } = new();
    }

    public sealed class Entry
    {
        [JsonPropertyName("vendor")]      public string Vendor { get; set; } = "";
        [JsonPropertyName("match")]       public string Match { get; set; } = "";
        [JsonPropertyName("side")]        public string Side { get; set; } = "both";   // client | server | both
        [JsonPropertyName("severity")]    public string Severity { get; set; } = "warn"; // info | warn | fail
        [JsonPropertyName("badVersions")] public List<string> BadVersions { get; set; } = new();
        [JsonPropertyName("range")]       public VersionRange? Range { get; set; }
        [JsonPropertyName("reason")]      public string Reason { get; set; } = "";
        [JsonPropertyName("fixedIn")]     public string FixedIn { get; set; } = "";
        [JsonPropertyName("link")]        public string Link { get; set; } = "";
    }

    public sealed class VersionRange
    {
        [JsonPropertyName("min")] public string Min { get; set; } = "";
        [JsonPropertyName("max")] public string Max { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // ── loading: remote (cached) → cache file → embedded ────────────────────

    /// <summary>Load the advisory list. Tries a fresh GitHub fetch (unless a recent cache exists),
    /// then the cache file, then the embedded copy. Never throws — returns the embedded list on any
    /// failure so evaluation always has something to work with.</summary>
    public static async Task<BadDriverList> LoadAsync(CancellationToken ct = default)
    {
        var cache = CachePath();
        try
        {
            if (File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < CacheTtl)
                return Parse(await File.ReadAllTextAsync(cache, ct)) ?? Embedded();
        }
        catch { /* fall through to fetch */ }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RDPeek");
            var json = await http.GetStringAsync(ListUrl, ct);
            var parsed = Parse(json);
            if (parsed is not null)
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(cache)!); await File.WriteAllTextAsync(cache, json, ct); }
                catch { /* cache is best-effort */ }
                return parsed;
            }
        }
        catch { /* offline / blocked / bad JSON — use cache or embedded */ }

        try { if (File.Exists(cache)) return Parse(await File.ReadAllTextAsync(cache, ct)) ?? Embedded(); }
        catch { }
        return Embedded();
    }

    /// <summary>The copy compiled into this assembly (data/bad-gfx-drivers.json). Always available.</summary>
    public static BadDriverList Embedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(EmbeddedName, StringComparison.OrdinalIgnoreCase));
            if (res is not null)
            {
                using var s = asm.GetManifestResourceStream(res)!;
                using var r = new StreamReader(s);
                return Parse(r.ReadToEnd()) ?? new BadDriverList();
            }
        }
        catch { }
        return new BadDriverList();
    }

    private static BadDriverList? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<BadDriverList>(json, JsonOpts); }
        catch { return null; }
    }

    private static string CachePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "RDPeek", EmbeddedName);

    // ── evaluation ──────────────────────────────────────────────────────────

    /// <summary>Evaluate a set of adapters against the list. Emits one advisory per matched bad-driver
    /// entry, plus at most one quiet "old driver" note per adapter (only when it wasn't already flagged
    /// as bad — so the old-driver check never piles onto a real warning or nags repeatedly).</summary>
    public static List<DriverAdvisory> Evaluate(BadDriverList list, IEnumerable<GpuDriver> gpus)
    {
        var outp = new List<DriverAdvisory>();
        if (list is null) return outp;

        foreach (var gpu in gpus)
        {
            bool flagged = false;
            foreach (var e in list.Entries)
            {
                if (!SideApplies(e.Side, gpu.Side)) continue;
                if (!NameMatches(e, gpu.Name)) continue;
                if (!VersionMatches(e, gpu.DriverVersion)) continue;
                flagged = true;
                string fix = string.IsNullOrEmpty(e.FixedIn) ? "" : $"  Fixed in {e.FixedIn} or later.";
                outp.Add(new DriverAdvisory(
                    Norm(e.Severity),
                    $"{gpu.Name} ({gpu.DriverVersion})",
                    gpu.Side,
                    $"{e.Reason}{fix}",
                    e.Link));
            }

            if (!flagged && list.OldDriverMonths > 0 && TryAge(gpu.DriverDate, out int months) && months >= list.OldDriverMonths)
                outp.Add(new DriverAdvisory(
                    "info",
                    $"{gpu.Name} ({gpu.DriverVersion})",
                    gpu.Side,
                    $"Display driver is ~{months / 12.0:0.#} years old (dated {gpu.DriverDate}). " +
                    "Not necessarily a problem, but a newer driver often fixes RDP graphics issues.",
                    ""));
        }
        return outp;
    }

    private static bool SideApplies(string entrySide, string gpuSide) =>
        string.IsNullOrEmpty(entrySide) || entrySide.Equals("both", StringComparison.OrdinalIgnoreCase)
        || entrySide.Equals(gpuSide, StringComparison.OrdinalIgnoreCase);

    private static bool NameMatches(Entry e, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!string.IsNullOrEmpty(e.Vendor) && name.IndexOf(e.Vendor, StringComparison.OrdinalIgnoreCase) < 0) return false;
        if (!string.IsNullOrEmpty(e.Match) && name.IndexOf(e.Match, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return !string.IsNullOrEmpty(e.Vendor) || !string.IsNullOrEmpty(e.Match);
    }

    private static bool VersionMatches(Entry e, string version)
    {
        if (e.BadVersions.Contains("*")) return true;                    // any version of this adapter
        if (string.IsNullOrWhiteSpace(version)) return false;
        if (e.BadVersions.Any(v => v.Equals(version, StringComparison.OrdinalIgnoreCase))) return true;
        if (e.Range is { } r && !string.IsNullOrEmpty(r.Min) && !string.IsNullOrEmpty(r.Max))
            return CompareVersions(version, r.Min) >= 0 && CompareVersions(version, r.Max) <= 0;
        return false;
    }

    /// <summary>Compare dotted-numeric driver versions (e.g. 31.0.15.3623). Non-numeric parts sort as 0.</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.'); var pb = b.Split('.');
        int n = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            long va = i < pa.Length && long.TryParse(pa[i], out var x) ? x : 0;
            long vb = i < pb.Length && long.TryParse(pb[i], out var y) ? y : 0;
            if (va != vb) return va < vb ? -1 : 1;
        }
        return 0;
    }

    private static bool TryAge(string date, out int months)
    {
        months = 0;
        if (!DateTime.TryParse(date, out var d)) return false;
        var now = DateTime.UtcNow;
        months = (now.Year - d.Year) * 12 + (now.Month - d.Month);
        return months >= 0;
    }

    private static string Norm(string sev) =>
        sev?.ToLowerInvariant() switch { "fail" => "fail", "info" => "info", _ => "warn" };
}
