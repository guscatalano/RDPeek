using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Rdpeek.Client;

/// <summary>The result of an update check.</summary>
public sealed record UpdateInfo(string Current, string Latest, bool UpdateAvailable, string Url, string PublishedAt);

/// <summary>
/// Checks GitHub Releases for a newer RDPeek than the running build. Best-effort and never throws —
/// returns null when offline, rate-limited, or the version can't be read. Shared by the Companion
/// (shows a quiet notice) and the Doctor (`rdpeek-doctor update`).
/// </summary>
public static class UpdateCheck
{
    public const string ReleasesApi = "https://api.github.com/repos/guscatalano/RDPeek/releases/latest";
    public const string ReleasesPage = "https://github.com/guscatalano/RDPeek/releases/latest";

    /// <summary>This build's version, as "x.y.z" (from the entry assembly). Falls back to the calling
    /// assembly. CI stamps it via -p:Version; a plain dev build is typically 1.0.0.</summary>
    public static string CurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetCallingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            // Strip any "+<git sha>" / "-<prerelease>" build metadata.
            int cut = info.IndexOfAny(new[] { '+', '-' });
            return (cut > 0 ? info[..cut] : info).Trim();
        }
        var v = asm.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    public static async Task<UpdateInfo?> CheckAsync(string? currentVersion = null, CancellationToken ct = default)
    {
        currentVersion ??= CurrentVersion();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RDPeek");           // GitHub API requires a UA
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync(ReleasesApi, ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
            string published = root.TryGetProperty("published_at", out var p) ? p.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(tag)) return null;

            string latest = tag.TrimStart('v', 'V');
            bool newer = DriverAdvisories.CompareVersions(latest, currentVersion) > 0;
            return new UpdateInfo(currentVersion, latest, newer, url, published);
        }
        catch { return null; }   // offline / rate-limited / bad payload — silently no-op
    }
}
