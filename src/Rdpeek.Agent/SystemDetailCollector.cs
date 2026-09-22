using System.Management;
using Dvc.Diag.Protocol;
using Microsoft.Win32;

namespace Rdpeek.Agent;

/// <summary>
/// Deep, slower-changing system detail: build identity (BuildLabEx and friends from the
/// registry), installed updates, display-adapter drivers, and PnP devices — the last three
/// via WMI. Every source is wrapped so one failure (WMI disabled, a locked key) degrades to
/// a note rather than taking the whole snapshot down.
/// </summary>
internal static class SystemDetailCollector
{
    public static SystemDetail Collect()
    {
        var d = new SystemDetail();
        Safe(d, "build", () => ReadBuildIdentity(d));
        Safe(d, "uptime", () => ReadUptime(d));
        Safe(d, "updates", () => ReadHotfixes(d));
        Safe(d, "gpus", () => ReadGpus(d));
        Safe(d, "devices", () => ReadDevices(d));
        return d;
    }

    private static void Safe(SystemDetail d, string what, Action collect)
    {
        try { collect(); }
        catch (Exception ex) { d.Notes.Add($"{what}: {ex.Message}"); }
    }

    private static void ReadBuildIdentity(SystemDetail d)
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (k is null) return;
        d.BuildLabEx = (k.GetValue("BuildLabEx") as string) ?? "";
        d.BuildLab = (k.GetValue("BuildLab") as string) ?? "";
        d.EditionId = (k.GetValue("EditionID") as string) ?? "";
        d.DisplayVersion = (k.GetValue("DisplayVersion") as string) ?? (k.GetValue("ReleaseId") as string) ?? "";
        d.RegisteredOwner = (k.GetValue("RegisteredOwner") as string) ?? "";

        // InstallDate is a Unix time (seconds) REG_DWORD.
        if (k.GetValue("InstallDate") is int installUnix && installUnix > 0)
            d.InstallDate = DateTimeOffset.FromUnixTimeSeconds(installUnix).LocalDateTime.ToString("yyyy-MM-dd");
    }

    private static void ReadUptime(SystemDetail d)
    {
        var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        d.BootTimeTicks = boot.Ticks;
        d.UptimeMs = Environment.TickCount64;
    }

    private static void ReadHotfixes(SystemDetail d)
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering");
        foreach (var mo in searcher.Get().Cast<ManagementObject>())
            d.Hotfixes.Add(new SystemDetail.Types.Hotfix
            {
                HotfixId = Str(mo["HotFixID"]),
                Description = Str(mo["Description"]),
                InstalledOn = Str(mo["InstalledOn"]),
            });

        // Newest KB first when the id is numeric (KB5031354 > KB5029263).
        var ordered = d.Hotfixes.OrderByDescending(h => KbNumber(h.HotfixId)).ToList();
        d.Hotfixes.Clear();
        d.Hotfixes.AddRange(ordered);
    }

    private static void ReadGpus(SystemDetail d)
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DriverVersion, DriverDate, AdapterRAM, Status FROM Win32_VideoController");
        foreach (var mo in searcher.Get().Cast<ManagementObject>())
            d.Gpus.Add(new SystemDetail.Types.Gpu
            {
                Name = Str(mo["Name"]),
                DriverVersion = Str(mo["DriverVersion"]),
                DriverDate = WmiDate(mo["DriverDate"]),
                VramBytes = mo["AdapterRAM"] is uint ram ? ram : 0,
                Status = Str(mo["Status"]),
            });
    }

    private static void ReadDevices(SystemDetail d)
    {
        // All PnP devices, but the interesting ones are those with a config-manager error.
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, PNPClass, Status, ConfigManagerErrorCode FROM Win32_PnPEntity");
        foreach (var mo in searcher.Get().Cast<ManagementObject>())
        {
            int err = mo["ConfigManagerErrorCode"] is int c ? c : 0;
            d.Devices.Add(new SystemDetail.Types.PnpDevice
            {
                Name = Str(mo["Name"]),
                DeviceClass = Str(mo["PNPClass"]),
                Status = Str(mo["Status"]),
                Problem = err != 0 ? $"CM_PROB {err}: {CmProblem(err)}" : "",
            });
        }

        // Problem devices first, then by class, so the failures are at the top.
        var ordered = d.Devices
            .OrderByDescending(x => x.Problem.Length > 0)
            .ThenBy(x => x.DeviceClass, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        d.Devices.Clear();
        d.Devices.AddRange(ordered);
    }

    private static string Str(object? o) => o?.ToString()?.Trim() ?? "";

    private static long KbNumber(string id)
    {
        var digits = new string(id.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var n) ? n : 0;
    }

    /// <summary>WMI CIM_DATETIME ("yyyymmddHHMMSS.ffffff+zzz") → yyyy-MM-dd.</summary>
    private static string WmiDate(object? o)
    {
        var s = o?.ToString();
        if (string.IsNullOrEmpty(s) || s.Length < 8) return "";
        try { return $"{s[..4]}-{s.Substring(4, 2)}-{s.Substring(6, 2)}"; }
        catch { return ""; }
    }

    // The common Device Manager error codes, decoded to something readable.
    private static string CmProblem(int code) => code switch
    {
        1 => "not configured correctly",
        3 => "driver corrupted / low memory",
        10 => "cannot start",
        12 => "not enough resources",
        14 => "needs a restart",
        18 => "reinstall the drivers",
        22 => "disabled",
        28 => "drivers not installed",
        31 => "driver failed to load",
        37 => "driver returned failure on init",
        43 => "reported problems (device stopped)",
        45 => "not connected",
        _ => "see Device Manager",
    };
}
