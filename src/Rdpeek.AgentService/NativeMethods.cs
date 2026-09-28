using System.Runtime.InteropServices;

namespace Rdpeek.AgentService;

// =====================================================================================
//  WHY THIS FILE EXISTS
// -------------------------------------------------------------------------------------
//  A Windows Service runs in **Session 0**, which is isolated and has **no interactive
//  RDP DVC**. So the service itself CANNOT open the diagnostics channel and cannot simply
//  spawn the agent into a user session with CreateProcessAsUser either: anything a
//  session-0 service creates in another session dies at desktop-attach (0xC0000142,
//  verified). So the service delegates the actual launch to the **Task Scheduler**, which
//  is the supported way to place a process in a user's session WITH its interactive
//  desktop (so the agent's systray works). The P/Invokes below are just what's left:
//    - WTSEnumerateSessions / WTSQuerySessionInformation : find active sessions + their user
//    - OpenProcess / GetExitCodeProcess / TerminateProcess : monitor & stop the agent proc
//  The task itself is registered/run/deleted via schtasks (see SessionLauncher).
// =====================================================================================
internal static class NativeMethods
{
    // ---- session enumeration (wtsapi32) ----

    internal enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive,          // a user is logged on AND the session is connected
        WTSConnected,
        WTSConnectQuery,
        WTSShadow,
        WTSDisconnected,    // logged on but the RDP/console session is detached
        WTSIdle,
        WTSListen,
        WTSReset,
        WTSDown,
        WTSInit,
    }

    // Subset of WTS_INFO_CLASS we use.
    internal enum WTS_INFO_CLASS
    {
        WTSUserName = 5,
        WTSDomainName = 7,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WTS_SESSION_INFO
    {
        public uint SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    internal static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool WTSEnumerateSessionsW(
        IntPtr hServer,
        int Reserved,
        int Version,
        out IntPtr ppSessionInfo,
        out int pCount);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool WTSQuerySessionInformationW(
        IntPtr hServer,
        uint SessionId,
        WTS_INFO_CLASS WTSInfoClass,
        out IntPtr ppBuffer,
        out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    internal static extern void WTSFreeMemory(IntPtr pMemory);

    // ---- process monitoring (kernel32) ----

    internal const uint PROCESS_TERMINATE = 0x0001;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint SYNCHRONIZE = 0x00100000;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    /// <summary>Query a single string session-info value (e.g. user name), or null.</summary>
    internal static string? QuerySessionString(uint sessionId, WTS_INFO_CLASS info)
    {
        if (!WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, sessionId, info, out IntPtr buf, out _))
            return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { WTSFreeMemory(buf); }
    }
}
