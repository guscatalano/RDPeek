using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Rdpeek.Companion;

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

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    public static List<RdpWindow> Enumerate()
    {
        var windows = new List<RdpWindow>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            int len = GetWindowTextLength(hwnd);
            if (len == 0) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            string procName;
            try { procName = Process.GetProcessById((int)pid).ProcessName; }
            catch { return true; }
            if (!procName.Equals("mstsc", StringComparison.OrdinalIgnoreCase)) return true;

            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            windows.Add(new RdpWindow(hwnd, (int)pid, title, ParseHost(title)));
            return true;
        }, IntPtr.Zero);

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
