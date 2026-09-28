using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Rdpeek.Agent;

/// <summary>
/// Keeps the in-session agent current. At <c>serve</c> startup — before any DVC channel is opened, so
/// there's never a second instance — it checks GitHub Releases for a newer <c>rdpeek-agent.exe</c>,
/// swaps its own binary, and re-execs. Best-effort and quiet on failure: if anything goes wrong
/// (offline, read-only path, rate-limited) it just keeps serving the current build. Disable with
/// <c>--no-update</c>.
/// </summary>
internal static class SelfUpdate
{
    private const string ReleasesApi = "https://api.github.com/repos/guscatalano/RDPeek/releases/latest";
    private const string AssetName = "rdpeek-agent.exe";

    /// <summary>Returns true if it downloaded a newer agent and launched it — the caller should then
    /// exit so the new process takes over.</summary>
    public static bool MaybeUpdateAndReexec(string[] args)
    {
        try
        {
            if (args.Any(a => a.Equals("--no-update", StringComparison.OrdinalIgnoreCase))) return false;
            // Set on the relaunched child so a mis-stamped version can't cause an update loop.
            if (Environment.GetEnvironmentVariable("RDPEEK_UPDATED") == "1") return false;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            try { var old = exe + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }

            var (latest, url) = GetLatest();
            string current = Current();
            if (latest is null || url is null) return false;
            if (Compare(latest, current) <= 0) { Console.WriteLine($"[update] agent v{current} is current."); return false; }

            Console.WriteLine($"[update] agent v{current} -> v{latest}: downloading…");
            var tmp = exe + ".new";
            if (!Download(url, tmp)) return false;
            if (new FileInfo(tmp).Length < 1_000_000) { try { File.Delete(tmp); } catch { } return false; }  // sanity

            // Rename the running image aside (allowed while mapped), drop the new one in its place.
            var aside = exe + ".old";
            try { if (File.Exists(aside)) File.Delete(aside); } catch { }
            File.Move(exe, aside);
            File.Move(tmp, exe);

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["RDPEEK_UPDATED"] = "1";
            Process.Start(psi);
            Console.WriteLine($"[update] relaunched agent v{latest}; exiting old process.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[update] skipped: {ex.Message}");
            return false;
        }
    }

    /// <summary>True when a client-advertised version is newer than this agent's build.</summary>
    public static bool ClientIsNewer(string clientVersion) => Compare(clientVersion, Current()) > 0;

    private static string Current()
    {
        var v = (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private static (string? latest, string? url) GetLatest()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RDPeek-Agent");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = http.GetStringAsync(ReleasesApi).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string? url = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                    if ((a.TryGetProperty("name", out var n) ? n.GetString() : null)?.Equals(AssetName, StringComparison.OrdinalIgnoreCase) == true)
                        url = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
            return (string.IsNullOrEmpty(tag) ? null : tag.TrimStart('v', 'V'), url);
        }
        catch { return (null, null); }
    }

    private static bool Download(string url, string path)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RDPeek-Agent");
            var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            File.WriteAllBytes(path, bytes);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Compare dotted-numeric versions (e.g. 0.3.3). Missing parts sort as 0.</summary>
    private static int Compare(string a, string b)
    {
        var pa = a.Split('.'); var pb = b.Split('.');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            long va = i < pa.Length && long.TryParse(pa[i], out var x) ? x : 0;
            long vb = i < pb.Length && long.TryParse(pb[i], out var y) ? y : 0;
            if (va != vb) return va < vb ? -1 : 1;
        }
        return 0;
    }
}
