using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rdpeek.AgentService;

// =====================================================================================
//  WHY THIS FILE EXISTS
// -------------------------------------------------------------------------------------
//  A Windows Service runs in **Session 0**, which is isolated and has **no interactive
//  RDP DVC**. So the service itself CANNOT open the diagnostics channel:
//  WTSVirtualChannelOpenEx(WTS_CURRENT_SESSION, ...) called from session 0 finds
//  nothing to attach to. The service's job is therefore to *launch the agent INTO each
//  interactive user session* and let that in-session process do the real DVC work.
//
//  That cross-session launch is the whole reason for the P/Invokes below:
//    - WTSEnumerateSessions / WTSQuerySessionInformation : find active user sessions
//    - WTSQueryUserToken                                  : get that session's user token
//    - CreateEnvironmentBlock                             : build the user's env block
//    - CreateProcessAsUser (lpDesktop = winsta0\default)  : spawn rdpeek-agent.exe there
//  All of this requires the service to run as LocalSystem (SeTcbPrivilege), i.e. admin.
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

    [DllImport("wtsapi32.dll")]
    internal static extern void WTSFreeMemory(IntPtr pMemory);

    // Retrieves the primary access token of the user logged on to the given session.
    // Fails (returns false) for sessions with no interactive user (e.g. the listener /
    // session 0), which is how we skip them.
    [DllImport("wtsapi32.dll", SetLastError = true)]
    internal static extern bool WTSQueryUserToken(uint SessionId, out SafeAccessTokenHandle phToken);

    // ---- environment block (userenv) ----

    [DllImport("userenv.dll", SetLastError = true)]
    internal static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, SafeAccessTokenHandle hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    internal static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    // ---- process creation (advapi32 / kernel32) ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;   // MUST be "winsta0\\default" to land on the user's interactive desktop
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    internal const uint CREATE_NO_WINDOW = 0x08000000;
    internal const uint CREATE_NEW_CONSOLE = 0x00000010;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcessAsUserW(
        SafeAccessTokenHandle hToken,
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
}
