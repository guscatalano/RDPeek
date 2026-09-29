using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using static Rdpeek.AgentService.NativeMethods;

namespace Rdpeek.AgentService;

/// <summary>
/// Launches <c>rdpeek-agent.exe serve</c> INTO an interactive user session on behalf of the
/// session-0 supervisor service. Two strategies, both landing on a monitorable process:
///
///   * <see cref="LaunchNative"/> (the supervisor's default): clone winlogon.exe's SYSTEM token
///     in the target session and CreateProcessAsUser with an EMPTY lpDesktop. Empty is the crux —
///     "winsta0\default" from session 0 resolves against the caller's window station and the child
///     dies at desktop-attach (0xC0000142); empty lets the system pick the token's-session desktop,
///     where the systray works. Runs as SYSTEM-in-session (the PsExec -s -i model).
///   * <see cref="LaunchViaTask"/> (fallback): register + run a per-session InteractiveToken
///     scheduled task (runs as the logged-on user, no stored password), then find the process.
///
/// Both return a <see cref="LaunchedAgent"/> the supervisor watches for exit and relaunches.
/// </summary>
internal static class SessionLauncher
{
    /// <summary>An interactive user session that is a candidate for an agent.</summary>
    internal readonly record struct SessionInfo(uint SessionId, string WinStation, NativeMethods.WTS_CONNECTSTATE_CLASS State);

    private const string TaskFolder = "RDPeek";

    /// <summary>Deterministic task name for a session, e.g. <c>RDPeek\Agent-S1</c>.</summary>
    internal static string TaskNameFor(uint sessionId) => $@"{TaskFolder}\Agent-S{sessionId}";

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
    /// DEFAULT launcher: start <c>rdpeek-agent serve</c> in <paramref name="sessionId"/> as SYSTEM,
    /// natively via CreateProcessAsUser using a token cloned from winlogon.exe in that session and an
    /// EMPTY lpDesktop. Returns the wrapped process. Throws if there's no winlogon in the session or
    /// any P/Invoke fails (the supervisor then falls back to <see cref="LaunchViaTask"/>).
    /// </summary>
    public static LaunchedAgent LaunchNative(uint sessionId, string agentExePath)
    {
        SafeAccessTokenHandle token = BuildSystemTokenForSession(sessionId);
        using (token)
        {
            if (!CreateEnvironmentBlock(out IntPtr envBlock, token, false))
                throw new InvalidOperationException(
                    $"CreateEnvironmentBlock failed for session {sessionId} (Win32 {Marshal.GetLastWin32Error()}).");
            try
            {
                var si = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFO>(),
                    // EMPTY lpDesktop is THE fix for 0xC0000142: "winsta0\default" from a session-0
                    // service resolves against the caller's window station (session 0), which the
                    // target-session token can't attach to. Empty => system assigns the token's-session
                    // desktop, so the process starts and the systray works.
                    lpDesktop = "",
                };
                // --no-update: the SERVICE owns the agent's lifecycle. Without it the agent could
                // self-update and re-exec, which the supervisor sees as an exit → transient double agent.
                string cmdLine = $"\"{agentExePath}\" serve --no-update";
                string workingDir = Path.GetDirectoryName(agentExePath) ?? Environment.SystemDirectory;
                uint flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;

                bool ok = CreateProcessAsUserW(
                    token, null, cmdLine, IntPtr.Zero, IntPtr.Zero,
                    false, flags, envBlock, workingDir, ref si, out PROCESS_INFORMATION pi);
                if (!ok)
                    throw new InvalidOperationException(
                        $"CreateProcessAsUser failed for session {sessionId} (Win32 {Marshal.GetLastWin32Error()}).");

                CloseHandle(pi.hThread);
                return new LaunchedAgent(sessionId, pi.dwProcessId, pi.hProcess);
            }
            finally
            {
                DestroyEnvironmentBlock(envBlock);
            }
        }
    }

    /// <summary>
    /// Clone winlogon.exe's token in <paramref name="sessionId"/> as a primary token — a genuine
    /// session-N SYSTEM token (winlogon runs as SYSTEM in every interactive session). SYSTEM already
    /// has full winsta0\default access there, so no ACL surgery is needed.
    /// </summary>
    private static SafeAccessTokenHandle BuildSystemTokenForSession(uint sessionId)
    {
        int winlogonPid = FindWinlogonPid(sessionId)
            ?? throw new InvalidOperationException($"No winlogon.exe in session {sessionId} (no interactive logon?).");

        IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)winlogonPid);
        if (hProc == IntPtr.Zero)
            throw new InvalidOperationException(
                $"OpenProcess(winlogon pid {winlogonPid}) failed (Win32 {Marshal.GetLastWin32Error()}). The service must run as LocalSystem.");
        try
        {
            if (!OpenProcessToken(hProc, TOKEN_DUPLICATE | TOKEN_QUERY, out SafeAccessTokenHandle winlogonToken))
                throw new InvalidOperationException($"OpenProcessToken(winlogon) failed (Win32 {Marshal.GetLastWin32Error()}).");
            using (winlogonToken)
            {
                if (!DuplicateTokenEx(winlogonToken, MAXIMUM_ALLOWED, IntPtr.Zero,
                        SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation, TOKEN_TYPE.TokenPrimary, out SafeAccessTokenHandle dup))
                    throw new InvalidOperationException($"DuplicateTokenEx(winlogon) failed (Win32 {Marshal.GetLastWin32Error()}).");
                return dup;
            }
        }
        finally { CloseHandle(hProc); }
    }

    private static int? FindWinlogonPid(uint sessionId)
    {
        foreach (var p in Process.GetProcessesByName("winlogon"))
        {
            try { if ((uint)p.SessionId == sessionId) return p.Id; }
            catch { /* access races */ }
            finally { p.Dispose(); }
        }
        return null;
    }

    /// <summary>
    /// FALLBACK launcher: register + run an on-demand scheduled task that starts <c>rdpeek-agent serve</c>
    /// as the logged-on user of <paramref name="sessionId"/>, then find and wrap the agent process so
    /// its exit can be watched. Throws if the session has no resolvable user, if schtasks fails,
    /// or if the agent process never appears.
    /// </summary>
    public static LaunchedAgent LaunchViaTask(uint sessionId, string agentExePath)
    {
        string user = ResolveSessionUser(sessionId)
            ?? throw new InvalidOperationException($"Could not resolve the logged-on user for session {sessionId}.");

        string taskName = TaskNameFor(sessionId);
        string workingDir = Path.GetDirectoryName(agentExePath) ?? Environment.SystemDirectory;

        // Register (overwrite) the task from an XML definition. XML avoids schtasks /TR quoting
        // pitfalls and lets us request LogonType=InteractiveToken (runs in the user's session,
        // no stored password) — the whole point of going through Task Scheduler.
        string xml = BuildTaskXml(user, agentExePath, workingDir);
        string xmlPath = Path.Combine(Path.GetTempPath(), $"rdpeek-agent-task-{sessionId}.xml");
        // schtasks wants a Unicode file.
        File.WriteAllText(xmlPath, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        try
        {
            var create = RunSchtasks("/Create", "/F", "/TN", taskName, "/XML", xmlPath);
            if (create.ExitCode != 0)
                throw new InvalidOperationException(
                    $"schtasks /Create failed for session {sessionId} (exit {create.ExitCode}): {create.Output.Trim()}");
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* best effort */ }
        }

        var launchUtc = DateTime.UtcNow;
        var run = RunSchtasks("/Run", "/TN", taskName);
        if (run.ExitCode != 0)
            throw new InvalidOperationException(
                $"schtasks /Run failed for session {sessionId} (exit {run.ExitCode}): {run.Output.Trim()}");

        // The task engine starts the process asynchronously; poll briefly for it in the target session.
        var (pid, hProcess) = FindAgentProcess(sessionId, launchUtc.AddSeconds(-2), TimeSpan.FromSeconds(12))
            ?? throw new InvalidOperationException(
                $"Agent task ran for session {sessionId} but no rdpeek-agent process appeared. " +
                "Check the task's LastTaskResult and that the user is interactively logged on.");

        return new LaunchedAgent(sessionId, pid, hProcess);
    }

    /// <summary>Delete the per-session task (best effort). Called on logoff / service stop.</summary>
    public static void RemoveTask(uint sessionId)
    {
        try { RunSchtasks("/Delete", "/F", "/TN", TaskNameFor(sessionId)); } catch { /* best effort */ }
    }

    /// <summary>domain\user (or .\user) for the session, or null if none is logged on.</summary>
    private static string? ResolveSessionUser(uint sessionId)
    {
        string? user = QuerySessionString(sessionId, WTS_INFO_CLASS.WTSUserName);
        if (string.IsNullOrEmpty(user)) return null;
        string? domain = QuerySessionString(sessionId, WTS_INFO_CLASS.WTSDomainName);
        return string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}";
    }

    /// <summary>Find the newest rdpeek-agent process in the session started at/after <paramref name="notBefore"/>.</summary>
    private static (int pid, IntPtr hProcess)? FindAgentProcess(uint sessionId, DateTime notBefore, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Process? best = null;
            foreach (var p in Process.GetProcessesByName("rdpeek-agent"))
            {
                try
                {
                    if ((uint)p.SessionId != sessionId) { p.Dispose(); continue; }
                    if (p.StartTime.ToUniversalTime() < notBefore) { p.Dispose(); continue; }
                    if (best is null || p.StartTime > best.StartTime) { best?.Dispose(); best = p; }
                    else p.Dispose();
                }
                catch { try { p.Dispose(); } catch { } }
            }

            if (best is not null)
            {
                int pid = best.Id;
                best.Dispose();
                // Reopen with exactly the rights we need (wait, query exit, terminate).
                IntPtr h = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE, false, (uint)pid);
                if (h != IntPtr.Zero) return (pid, h);
                // Raced with exit; keep trying until the deadline.
            }

            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(250);
        }
    }

    private static string BuildTaskXml(string userId, string command, string workingDir)
    {
        // Task Scheduler 1.2 schema. InteractiveToken => runs in the user's session (their desktop),
        // no stored credentials. ExecutionTimeLimit PT0S = run indefinitely. AllowStartOnDemand so /Run works.
        string esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;
        return
$@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>RDPeek in-session diagnostics agent (managed by RdpeekAgentSvc).</Description>
    <Author>RdpeekAgentSvc</Author>
  </RegistrationInfo>
  <Principals>
    <Principal id=""Author"">
      <UserId>{esc(userId)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{esc(command)}</Command>
      <Arguments>serve --no-update</Arguments>
      <WorkingDirectory>{esc(workingDir)}</WorkingDirectory>
    </Exec>
  </Actions>
  <Triggers />
</Task>";
    }

    private readonly record struct SchtasksResult(int ExitCode, string Output);

    private static SchtasksResult RunSchtasks(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start schtasks.exe.");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        return new SchtasksResult(proc.HasExited ? proc.ExitCode : -1, stdout + stderr);
    }
}

/// <summary>
/// Handle to the agent the service started in a session (via a scheduled task). Owns a process
/// handle for waiting/terminating, exposes a Task that completes on exit, and captures the exit code.
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

    /// <summary>The process exit code, captured when it exits (null while still running).</summary>
    public uint? ExitCode { get; private set; }

    /// <summary>Completes when the agent process exits (or when the handle is disposed).</summary>
    public Task ExitTask => _exited.Task;

    internal LaunchedAgent(uint sessionId, int processId, IntPtr hProcess)
    {
        SessionId = sessionId;
        ProcessId = processId;
        _hProcess = hProcess;

        _exitEvent = new ManualResetEvent(false)
        {
            SafeWaitHandle = new SafeWaitHandle(hProcess, ownsHandle: false),
        };
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _exitEvent,
            (_, _) =>
            {
                if (NativeMethods.GetExitCodeProcess(_hProcess, out uint code)) ExitCode = code;
                _exited.TrySetResult();
            },
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: true);
    }

    /// <summary>Forcibly terminate the agent (used on service stop / session logoff).</summary>
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
