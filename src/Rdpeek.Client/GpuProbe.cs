using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;

namespace Rdpeek.Client;

/// <summary>Reads this (client) machine's display adapters + driver info via WMI, reduced to the same
/// <see cref="GpuDriver"/> shape the agent reports for the session host — so the same
/// <see cref="DriverAdvisories"/> logic evaluates both sides. Client-side needs no elevation.</summary>
public static class GpuProbe
{
    public static List<GpuDriver> Local()
    {
        var gpus = new List<GpuDriver>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController");
            foreach (var mo in searcher.Get().Cast<ManagementObject>())
                gpus.Add(new GpuDriver(
                    Str(mo["Name"]),
                    Str(mo["DriverVersion"]),
                    WmiDate(mo["DriverDate"]),
                    "client"));
        }
        catch { /* WMI unavailable — no client-side advisories, agent side still evaluated */ }
        return gpus;
    }

    private static string Str(object? o) => o?.ToString() ?? "";

    /// <summary>WMI CIM_DATETIME ("yyyymmddHHMMSS.ffffff+zzz") → yyyy-MM-dd, matching the agent.</summary>
    private static string WmiDate(object? o)
    {
        var s = o?.ToString();
        if (string.IsNullOrEmpty(s) || s.Length < 8) return "";
        return DateTime.TryParseExact(s.Substring(0, 8), "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd") : "";
    }
}
