using Dvc.Diag.Protocol;
using Rdpeek.Protocol;

namespace Rdpeek.Agent;

/// <summary>
/// The agent's file-transfer security boundary: reads the real disk but only within the advertised
/// roots. Every path is resolved to a full path (collapsing <c>..</c>) before the root check, so a
/// traversal like <c>C:\diag\..\Windows\...</c> can't escape. Read-only — matches the shipped
/// pull-only posture.
/// </summary>
internal sealed class DiskFileSource : IFileSource
{
    private readonly string[] _roots;

    public DiskFileSource(IEnumerable<string> roots) =>
        _roots = roots.Select(NormalizeRoot).Where(r => r.Length > 0).Distinct().ToArray();

    public FileSourceResult OpenRead(string path, out Stream? stream, out long size)
    {
        stream = null;
        size = 0;
        if (!TryResolve(path, out var full)) return FileSourceResult.NotAllowed;
        if (!File.Exists(full)) return FileSourceResult.NotFound;
        try
        {
            var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream = fs;
            size = fs.Length;
            return FileSourceResult.Ok;
        }
        catch
        {
            return FileSourceResult.NotFound;
        }
    }

    public FileSourceResult List(string path, out FileList? list)
    {
        list = null;
        if (!TryResolve(path, out var full)) return FileSourceResult.NotAllowed;
        if (!Directory.Exists(full)) return FileSourceResult.NotFound;

        var fl = new FileList { Path = full };
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(full))
            {
                var di = new DirectoryInfo(dir);
                fl.Items.Add(new FileList.Types.Item { Name = di.Name, IsDir = true, MtimeTicks = di.LastWriteTimeUtc.Ticks });
            }
            foreach (var file in Directory.EnumerateFiles(full))
            {
                var fi = new FileInfo(file);
                fl.Items.Add(new FileList.Types.Item
                {
                    Name = fi.Name,
                    IsDir = false,
                    Size = (ulong)Math.Max(0, fi.Length),
                    MtimeTicks = fi.LastWriteTimeUtc.Ticks,
                });
            }
        }
        catch
        {
            return FileSourceResult.NotFound;
        }

        list = fl;
        return FileSourceResult.Ok;
    }

    /// <summary>Resolve to a full path and require it be at or under one of the roots.</summary>
    private bool TryResolve(string path, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { full = Path.GetFullPath(path); }
        catch { return false; }

        foreach (var root in _roots)
        {
            var f = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(f, r, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string NormalizeRoot(string root)
    {
        try
        {
            var full = Path.GetFullPath(root);
            return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
        }
        catch
        {
            return "";
        }
    }
}
