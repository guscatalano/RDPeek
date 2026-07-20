using System.Runtime.InteropServices;
using System.Text;

namespace Rdpeek.Agent;

/// <summary>Win32 / WTS interop used by the agent's collectors.</summary>
internal static class NativeApi
{
    // ---- current session constant ----
    internal const uint WTS_CURRENT_SESSION = 0xFFFFFFFF;
    internal const uint WTS_ANY_SESSION = 0xFFFFFFFE;
    internal static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;

    // ---- CPU times ----
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    // ---- memory ----
    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ---- WTS session information ----
    internal enum WTS_INFO_CLASS
    {
        WTSClientDisplay = 5,
        WTSClientName = 10,
        WTSClientProtocolType = 16,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WTS_CLIENT_DISPLAY
    {
        public uint HorizontalResolution;
        public uint VerticalResolution;
        public uint ColorDepth;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSQuerySessionInformationW(
        IntPtr hServer, uint sessionId, WTS_INFO_CLASS infoClass,
        out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    internal static extern void WTSFreeMemory(IntPtr pMemory);

    internal static string QuerySessionString(WTS_INFO_CLASS infoClass)
    {
        if (!WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION, infoClass, out var p, out _))
            return "";
        try { return Marshal.PtrToStringUni(p) ?? ""; }
        finally { WTSFreeMemory(p); }
    }

    internal static ushort QuerySessionUShort(WTS_INFO_CLASS infoClass)
    {
        if (!WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION, infoClass, out var p, out _))
            return 0;
        try { return (ushort)Marshal.ReadInt16(p); }
        finally { WTSFreeMemory(p); }
    }

    internal static WTS_CLIENT_DISPLAY QuerySessionDisplay()
    {
        if (!WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION, WTS_INFO_CLASS.WTSClientDisplay, out var p, out _))
            return default;
        try { return Marshal.PtrToStructure<WTS_CLIENT_DISPLAY>(p); }
        finally { WTSFreeMemory(p); }
    }

    // ---- WTS process enumeration (level 1 = WTS_PROCESS_INFO_EX) ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WTS_PROCESS_INFO_EXW
    {
        public uint SessionId;
        public uint ProcessId;
        public IntPtr pProcessName;   // LPWSTR
        public IntPtr pUserSid;       // PSID
        public uint NumberOfThreads;
        public uint HandleCount;
        public uint PagefileUsage;
        public uint PeakPagefileUsage;
        public uint WorkingSetSize;
        public uint PeakWorkingSetSize;
        public long UserTime;         // LARGE_INTEGER
        public long KernelTime;       // LARGE_INTEGER
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSEnumerateProcessesExW(
        IntPtr hServer, ref uint pLevel, uint sessionId, out IntPtr ppProcessInfo, out uint pCount);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSFreeMemoryExW(int wtsTypeClass, IntPtr pMemory, uint numberOfEntries);

    internal const int WTSTypeProcessInfoLevel1 = 1;

    // ---- SID resolution ----
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountSidW(
        string? lpSystemName, IntPtr sid,
        StringBuilder? name, ref uint cchName,
        StringBuilder? referencedDomainName, ref uint cchReferencedDomainName,
        out int peUse);

    internal static string ResolveSid(IntPtr pSid)
    {
        if (pSid == IntPtr.Zero) return "";
        uint cchName = 256, cchDomain = 256;
        var name = new StringBuilder((int)cchName);
        var domain = new StringBuilder((int)cchDomain);
        if (LookupAccountSidW(null, pSid, name, ref cchName, domain, ref cchDomain, out _))
            return domain.Length > 0 ? $"{domain}\\{name}" : name.ToString();
        return "";
    }
}
