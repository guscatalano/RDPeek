using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using static Rdpeek.AgentService.NativeMethods;

namespace Rdpeek.AgentService;

/// <summary>
/// The P/Invoke half of the supervisor: it enumerates interactive user sessions and
/// launches <c>rdpeek-agent.exe serve</c> INTO a given session as that session's user.
///
/// Remember the session-0 constraint (see NativeMethods): the service is in session 0
/// and cannot open the DVC itself, so everything here is about getting the agent to run
/// in the *user's* session, where WTSVirtualChannelOpenEx(WTS_CURRENT_SESSION, ...) can
/// actually find the channel.
/// </summary>
internal static class SessionLauncher
{
    /// <summary>An interactive user session that is a candidate for an agent.</summary>
    internal readonly record struct SessionInfo(uint SessionId, string WinStation, NativeMethods.WTS_CONNECTSTATE_CLASS State);

    /// <summary>
    /// Enumerate all sessions and return the *active* user sessions (RDP or console).
    /// Session 0 (services / the listener) is never returned. WTSActive means a user is
    /// logged on and the session is connected — exactly where we want the agent.
    /// </summary>
    public static IReadOnlyList<SessionInfo> EnumerateActiveUserSessions()
    {
        var result = new List<SessionInfo>();
        if (!WTSEnumerateSessionsW(WTS_CURRENT_SERVER_HANDLE, 0, 1, out var ppInfo, out int count))
            return result;

        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var infoPtr = ppInfo + (i * size);
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(infoPtr);

                // Skip session 0 (services) and anything that isn't a connected user session.
                if (info.SessionId == 0) continue;
                if (info.State != WTS_CONNECTSTATE_CLASS.WTSActive) continue;

                result.Add(new SessionInfo(info.SessionId, info.pWinStationName ?? "", info.State));
            }
        }
        finally
        {
            WTSFreeMemory(ppInfo);
        }

        return result;
    }

    /// <summary>
    /// Launch <c>rdpeek-agent.exe serve</c> in <paramref name="sessionId"/> as the logged-on
    /// user. Returns a <see cref="LaunchedAgent"/> whose <see cref="LaunchedAgent.ExitTask"/>
    /// completes when the agent process exits. Throws on any P/Invoke failure.
    /// </summary>
    public static LaunchedAgent Launch(uint sessionId, string agentExePath)
    {
        // 1) Get the primary token of the user in that session. Fails for sessions with no
        //    interactive user — the caller only passes WTSActive sessions, so this normally
        //    succeeds, but a session can log off between enumeration and here.
        if (!WTSQueryUserToken(sessionId, out SafeAccessTokenHandle userToken))
            throw new InvalidOperationException(
                $"WTSQueryUserToken failed for session {sessionId} (Win32 {Marshal.GetLastWin32Error()}). " +
                "The service must run as LocalSystem (SeTcbPrivilege).");

        using (userToken)
        {
            // 2) Build the user's environment block so the agent sees the user's env
            //    (LOCALAPPDATA, PATH, ...), not session 0's.
            if (!CreateEnvironmentBlock(out IntPtr envBlock, userToken, false))
                throw new InvalidOperationException(
                    $"CreateEnvironmentBlock failed for session {sessionId} (Win32 {Marshal.GetLastWin32Error()}).");

            try
            {
                // The agent is a console app; a service has no console, so hand it valid std handles
                // (the NUL sink). Without them the child's stdio is invalid and its startup aborts in
                // ~0s (verified on a live box: with valid handles it serves fine, without it exits 0x0).
                var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
                IntPtr hNul = CreateFileW("NUL", GENERIC_WRITE, FILE_SHARE_READ_WRITE, ref sa, OPEN_EXISTING, 0, IntPtr.Zero);
                bool haveNul = hNul != IntPtr.Zero && hNul != new IntPtr(-1);

                var si = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFO>(),
                    // MUST be the interactive desktop, or CreateProcessAsUser fails / the
                    // process has no desktop.
                    lpDesktop = @"winsta0\default",
                    // Give the child valid stdout/stderr (NUL). stdin stays null on purpose: NUL is opened
                    // write-only, and a write-only handle as stdin breaks console startup (verified — with
                    // stdin=0 + valid stdout/stderr the agent serves; with stdin=NUL-write it exits 0s).
                    dwFlags = haveNul ? (int)STARTF_USESTDHANDLES : 0,
                    hStdInput = IntPtr.Zero,
                    hStdOutput = haveNul ? hNul : IntPtr.Zero,
                    hStdError = haveNul ? hNul : IntPtr.Zero,
                };

                // Quote the exe path (it may contain spaces) and append the subcommand.
                string cmdLine = $"\"{agentExePath}\" serve";

                // CREATE_UNICODE_ENVIRONMENT is required because CreateEnvironmentBlock
                // returns a Unicode block. CREATE_NO_WINDOW keeps the console agent headless.
                uint flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;

                string workingDir = Path.GetDirectoryName(agentExePath) ?? Environment.SystemDirectory;

                PROCESS_INFORMATION pi;
                bool ok;
                try
                {
                    ok = CreateProcessAsUserW(
                        userToken,
                        null,               // lpApplicationName — taken from lpCommandLine's first token
                        cmdLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        haveNul,            // inherit handles only when we're passing the NUL std handles
                        flags,
                        envBlock,
                        workingDir,
                        ref si,
                        out pi);
                }
                finally
                {
                    // The child has its own reference to NUL now; drop ours.
                    if (haveNul) CloseHandle(hNul);
                }

                if (!ok)
                    throw new InvalidOperationException(
                        $"CreateProcessAsUser failed for session {sessionId} (Win32 {Marshal.GetLastWin32Error()}).");

                // We keep hProcess (for wait + terminate); the thread handle is not needed.
                CloseHandle(pi.hThread);
                return new LaunchedAgent(sessionId, pi.dwProcessId, pi.hProcess);
            }
            finally
            {
                DestroyEnvironmentBlock(envBlock);
            }
        }
    }
}

/// <summary>
/// Handle to an agent the service spawned into a session. Owns the process handle,
/// exposes a Task that completes on exit, and can terminate the process on service stop.
/// </summary>
internal sealed class LaunchedAgent : IDisposable
{
    private readonly IntPtr _hProcess;
    private readonly ManualResetEvent _exitEvent;
    private RegisteredWaitHandle? _registration;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public uint SessionId { get; }
    public int ProcessId { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>Completes when the agent process exits (or when the handle is disposed).</summary>
    public Task ExitTask => _exited.Task;

    internal LaunchedAgent(uint sessionId, int processId, IntPtr hProcess)
    {
        SessionId = sessionId;
        ProcessId = processId;
        _hProcess = hProcess;

        // Wrap the process handle in a waitable so the thread pool signals us on exit
        // without a dedicated blocking thread per agent.
        _exitEvent = new ManualResetEvent(false)
        {
            SafeWaitHandle = new SafeWaitHandle(hProcess, ownsHandle: false),
        };
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _exitEvent,
            (_, _) => _exited.TrySetResult(),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: true);
    }

    /// <summary>Forcibly terminate the agent (used on service stop).</summary>
    public void Terminate()
    {
        try { TerminateProcess(_hProcess, 0); } catch { /* already gone */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _registration?.Unregister(null);
        _registration = null;
        _exited.TrySetResult();
        _exitEvent.Dispose();          // does not own the handle
        NativeMethods.CloseHandle(_hProcess);
    }
}
