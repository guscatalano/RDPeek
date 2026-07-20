using System.Diagnostics;
using System.Runtime.InteropServices;
using Dvc.Diag.Protocol;
using Microsoft.Win32;
using static Rdpeek.Agent.NativeApi;

namespace Rdpeek.Agent;

/// <summary>Gathers a live host/session snapshot from Windows APIs and the registry.</summary>
internal static class SysInfoCollector
{
    public static SysInfoSnapshot Collect()
    {
        var snap = new SysInfoSnapshot
        {
            UptimeMs = (ulong)Environment.TickCount64,
            CpuLogical = (uint)Environment.ProcessorCount,
            CpuPercent = SampleCpuPercent(),
            HostName = Environment.MachineName,
            UserName = Environment.UserName,
            SessionId = (uint)Process.GetCurrentProcess().SessionId,
        };

        ReadOsInfo(snap);
        ReadCpuName(snap);
        ReadMemory(snap);
        ReadSessionInfo(snap);
        return snap;
    }

    private static void ReadOsInfo(SysInfoSnapshot snap)
    {
        using var k = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (k is null) return;

        if (uint.TryParse(k.GetValue("CurrentBuild") as string, out var build)) snap.OsBuild = build;
        if (k.GetValue("UBR") is int ubr) snap.OsUbr = (uint)ubr;
        snap.OsDisplayVer = (k.GetValue("DisplayVersion") as string) ?? (k.GetValue("ReleaseId") as string) ?? "";
        snap.OsEdition = (k.GetValue("EditionID") as string) ?? "";

        // The ProductName value still reads "Windows 10" on Windows 11 — a known
        // registry quirk. Build 22000+ is Windows 11; correct the label from the build.
        var product = (k.GetValue("ProductName") as string) ?? "";
        if (snap.OsBuild >= 22000 && product.Contains("Windows 10"))
            product = product.Replace("Windows 10", "Windows 11");
        snap.OsProductName = product;
    }

    private static void ReadCpuName(SysInfoSnapshot snap)
    {
        using var k = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        snap.CpuName = ((k?.GetValue("ProcessorNameString") as string) ?? "").Trim();
    }

    private static void ReadMemory(SysInfoSnapshot snap)
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            snap.MemTotalBytes = mem.ullTotalPhys;
            snap.MemAvailBytes = mem.ullAvailPhys;
        }
    }

    private static void ReadSessionInfo(SysInfoSnapshot snap)
    {
        var protocol = QuerySessionUShort(WTS_INFO_CLASS.WTSClientProtocolType);
        snap.Protocol = protocol switch
        {
            2 => "RDP",
            1 => "ICA",
            0 => "Console",
            _ => "",
        };

        // The client name / display describe the *remote* endpoint and are only
        // meaningful in a remote (RDP/ICA) session — for a console session they are
        // unpopulated/garbage, so leave them cleared.
        if (protocol is 2 or 1)
        {
            snap.ClientName = QuerySessionString(WTS_INFO_CLASS.WTSClientName);
            var disp = QuerySessionDisplay();
            snap.ScreenWidth = disp.HorizontalResolution;
            snap.ScreenHeight = disp.VerticalResolution;
        }
    }

    /// <summary>Whole-system busy % sampled over a short interval via GetSystemTimes.</summary>
    private static double SampleCpuPercent()
    {
        if (!GetSystemTimes(out long idle0, out long kern0, out long user0)) return 0;
        Thread.Sleep(200);
        if (!GetSystemTimes(out long idle1, out long kern1, out long user1)) return 0;

        long idle = idle1 - idle0;
        long total = (kern1 - kern0) + (user1 - user0); // kernel time already includes idle
        return total > 0 ? Math.Round((1.0 - (double)idle / total) * 100.0, 1) : 0;
    }
}
