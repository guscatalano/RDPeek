using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rdpeek.AgentService;

// =====================================================================================
//  WHY THIS FILE EXISTS
// -------------------------------------------------------------------------------------
//  A Windows Service runs in **Session 0**, isolated with no interactive RDP DVC, so it
//  must launch the agent INTO each interactive user session. There are two ways to do it,
//  and the service uses both (native first, Task Scheduler as fallback):
//
//   * NATIVE (default): clone winlogon.exe's SYSTEM token in the target session and
//     CreateProcessAsUser with an **empty lpDesktop**. The empty lpDesktop is the crux —
//     passing "winsta0\default" from session 0 resolves against the caller's (session 0)
//     window station and the child dies at desktop-attach (0xC0000142). Empty lets the
//     system assign the token's-session desktop, where the agent's systray works.
//   * TASK (fallback): register + run a per-session InteractiveToken scheduled task.
//
//  P/Invokes below serve both: session enumeration / user lookup, winlogon-token cloning +
//  CreateProcessAsUser (native), and OpenProcess/GetExitCode/Terminate (monitor & stop).
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

    // ---- native in-session launch: clone a session token + CreateProcessAsUser ----

    internal const uint TOKEN_DUPLICATE = 0x0002;
    internal const uint TOKEN_QUERY = 0x0008;
    internal const uint MAXIMUM_ALLOWED = 0x02000000;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    internal const uint CREATE_NO_WINDOW = 0x08000000;

    internal enum SECURITY_IMPERSONATION_LEVEL { SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation }
    internal enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation }

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out SafeAccessTokenHandle TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL ImpersonationLevel, TOKEN_TYPE TokenType, out SafeAccessTokenHandle phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    internal static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, SafeAccessTokenHandle hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    internal static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;   // EMPTY = system-assigns the token's-session desktop (the 0xC0000142 fix)
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

    /// <summary>Query a single string session-info value (e.g. user name), or null.</summary>
    internal static string? QuerySessionString(uint sessionId, WTS_INFO_CLASS info)
    {
        if (!WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, sessionId, info, out IntPtr buf, out _))
            return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { WTSFreeMemory(buf); }
    }
}
