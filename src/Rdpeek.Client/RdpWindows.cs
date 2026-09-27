using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Rdpeek.Client;

/// <summary>One open mstsc (RDP client) window = one connection context.</summary>
public sealed record RdpWindow(IntPtr Hwnd, int Pid, string Title, string Host);

/// <summary>Enumerates the RDP client windows currently open on this machine.</summary>
public static class RdpWindows
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lparam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>Pull a window to the foreground from another process. Windows blocks a plain
    /// SetForegroundWindow across the foreground-lock, so we briefly attach our input queue to the
    /// current foreground thread (and the target's) — the standard trick. Lets the switcher focus a
    /// session even when the in-process window plugin isn't loaded (which is what gives fullscreen).</summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        const int SW_RESTORE = 9;
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        uint thisThread = GetCurrentThreadId();

        if (fgThread != 0 && fgThread != thisThread) AttachThreadInput(thisThread, fgThread, true);
        if (targetThread != 0 && targetThread != thisThread && targetThread != fgThread)
            AttachThreadInput(thisThread, targetThread, true);
        try
        {
            ShowWindow(hwnd, SW_RESTORE);   // un-minimize if needed
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (fgThread != 0 && fgThread != thisThread) AttachThreadInput(thisThread, fgThread, false);
            if (targetThread != 0 && targetThread != thisThread && targetThread != fgThread)
                AttachThreadInput(thisThread, targetThread, false);
        }
    }

    // RDP client processes we recognize. Classic mstsc hosts each connection in a stable
    // "TscShellContainerClass" top-level window. The modern client (msrdc — the "Remote Desktop" /
    // Windows Desktop / AVD client) is WinUI-based and has no stable, semantic session-window class,
    // so — as every window-manager script that supports it does — we match it by process and pick its
    // largest real top-level window, excluding chrome (dialogs, the connection bar, our overlay).
    private static readonly System.Collections.Generic.HashSet<string> ClientProcesses =
        new(StringComparer.OrdinalIgnoreCase) { "mstsc", "msrdc" };

    // The one class we key mstsc off precisely; other mstsc windows (BBar, tooltips) are not connections.
    private const string MstscSessionClass = "TscShellContainerClass";

    // Never a session window, whatever the client: credential/other dialogs, the fullscreen connection
    // bar, and RDPeek's own overlay chip.
    private static readonly System.Collections.Generic.HashSet<string> ChromeClasses =
        new(StringComparer.Ordinal) { "#32770", "BBarWindowClass", "TscConnBarWndClass", "RdpeekOverlay" };

    public static List<RdpWindow> Enumerate()
    {
        // Gather candidates first, then reduce msrdc's several windows (connection center, session,
        // dialogs) down to one session window per process by picking the largest.
        var candidates = new List<(RdpWindow Win, string Proc, long Area)>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            int len = GetWindowTextLength(hwnd);
            if (len == 0) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            string procName;
            try { procName = Process.GetProcessById((int)pid).ProcessName; }
            catch { return true; }
            if (!ClientProcesses.Contains(procName)) return true;

            var clsSb = new StringBuilder(128);
            GetClassName(hwnd, clsSb, clsSb.Capacity);
            var cls = clsSb.ToString();
            if (ChromeClasses.Contains(cls)) return true;

            bool isMstsc = procName.Equals("mstsc", StringComparison.OrdinalIgnoreCase);
            // mstsc: only the exact session class counts. msrdc: any non-chrome titled window is a
            // candidate (we can't rely on its class), narrowed to the largest below.
            if (isMstsc && !cls.Equals(MstscSessionClass, StringComparison.Ordinal)) return true;

            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();

            long area = 0;
            if (GetWindowRect(hwnd, out var r))
                area = (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);

            candidates.Add((new RdpWindow(hwnd, (int)pid, title, ParseHost(title)), procName, area));
            return true;
        }, IntPtr.Zero);

        var windows = new List<RdpWindow>();
        // One session window per process: for mstsc each PID already yields a single session-class
        // window; for msrdc we keep only the largest window of each PID (the session, not its chrome).
        foreach (var group in candidates.GroupBy(c => c.Win.Pid))
        {
            var pick = group.OrderByDescending(c => c.Area).First();
            windows.Add(pick.Win);
        }

        return windows;
    }

    /// <summary>Best-effort host name from a window title like "HOST - Remote Desktop Connection".</summary>
    public static string ParseHost(string title)
    {
        foreach (var sep in new[] { " - ", " – ", " — " }) // hyphen, en dash, em dash
        {
            int i = title.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0) return title[..i].Trim();
        }
        return title.Trim();
    }
}
