using System.Runtime.InteropServices;

namespace Rdpeek.Companion.WinUI;

/// <summary>Current check-state for the tray menu.</summary>
public readonly record struct TrayState(bool DockRight, bool AutoCycle, bool ConnectionBar, bool Floating, bool Hotkeys, bool Startup);

/// <summary>
/// A Win32 system-tray icon for the Companion (WinUI 3 has no built-in NotifyIcon). It hosts a
/// message-only window on the UI thread — whose messages the WinUI message loop already pumps — adds a
/// Shell_NotifyIcon, and shows a classic popup menu on right-click. Left double-click runs the default
/// action. All menu ids are reported back through <paramref name="onCommand"/>; live check-state comes
/// from <paramref name="getState"/> each time the menu opens.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public const int CmdShowSwitcher = 1, CmdDockLeft = 2, CmdDockRight = 3,
                     CmdAutoCycle = 4, CmdConnectionBar = 5, CmdDashboard = 6, CmdExit = 7, CmdLaunch = 8,
                     CmdFloating = 9, CmdHotkeys = 10, CmdStartup = 11, CmdCheckUpdates = 12, CmdBalloonClicked = 13;

    private const int WM_TRAY = 0x8000 + 1;   // WM_APP + 1
    private const int WM_RBUTTONUP = 0x0205, WM_LBUTTONDBLCLK = 0x0203, WM_CONTEXTMENU = 0x007B, WM_NULL = 0;
    private const int NIN_BALLOONUSERCLICK = 0x0405;   // user clicked the balloon body (not the X)
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const int NIIF_INFO = 1;
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 8;
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize; public IntPtr hWnd; public int uID; public int uFlags;
        public int uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(int ex, string cls, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? n);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(int msg, ref NOTIFYICONDATAW d);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int ExtractIconExW(string exe, int index, IntPtr[]? large, IntPtr[]? small, int count);
    [DllImport("user32.dll")] private static extern IntPtr LoadIconW(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, uint id, string? item);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    private readonly Func<TrayState> _getState;
    private readonly Action<int> _onCommand;
    private readonly string _tip;
    private readonly WndProc _proc;   // kept alive
    private IntPtr _hwnd;
    private NOTIFYICONDATAW _data;

    public TrayIcon(Func<TrayState> getState, Action<int> onCommand, string? tip = null)
    {
        _getState = getState;
        _onCommand = onCommand;
        _tip = string.IsNullOrEmpty(tip) ? "RDPeek — RDP window switcher" : tip;
        _proc = WndProcImpl;

        var hinst = GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hinst,
            lpszClassName = "RdpeekTrayWnd",
        };
        RegisterClassExW(ref wc);
        _hwnd = CreateWindowExW(0, "RdpeekTrayWnd", "", 0, 0, 0, 0, 0, new IntPtr(-3) /*HWND_MESSAGE*/, IntPtr.Zero, hinst, IntPtr.Zero);

        IntPtr icon = IntPtr.Zero;
        try { var sm = new IntPtr[1]; if (ExtractIconExW(Environment.ProcessPath ?? "", 0, null, sm, 1) > 0) icon = sm[0]; }
        catch { icon = IntPtr.Zero; }
        if (icon == IntPtr.Zero) icon = LoadIconW(IntPtr.Zero, new IntPtr(32512) /*IDI_APPLICATION*/);

        _data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = icon,
            szTip = _tip,
        };
        Shell_NotifyIconW(NIM_ADD, ref _data);
    }

    private IntPtr WndProcImpl(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == WM_TRAY)
        {
            int mouse = (int)(l.ToInt64() & 0xFFFF);
            if (mouse == WM_RBUTTONUP || mouse == WM_CONTEXTMENU) ShowMenu();
            else if (mouse == WM_LBUTTONDBLCLK) _onCommand(CmdShowSwitcher);
            else if (mouse == NIN_BALLOONUSERCLICK) _onCommand(CmdBalloonClicked);
            return IntPtr.Zero;
        }
        return DefWindowProcW(h, msg, w, l);
    }

    private void ShowMenu()
    {
        var s = _getState();
        var menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, CmdShowSwitcher, "Show switcher");
        AppendMenuW(menu, MF_STRING, CmdLaunch, "Launch connections");
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING | (s.DockRight ? 0 : MF_CHECKED), CmdDockLeft, "Dock left");
        AppendMenuW(menu, MF_STRING | (s.DockRight ? MF_CHECKED : 0), CmdDockRight, "Dock right");
        AppendMenuW(menu, MF_STRING | (s.Floating ? MF_CHECKED : 0), CmdFloating, "Floating button");
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING | (s.Hotkeys ? MF_CHECKED : 0), CmdHotkeys, "Switch hotkeys (Ctrl+Alt+←/→)");
        AppendMenuW(menu, MF_STRING | (s.AutoCycle ? MF_CHECKED : 0), CmdAutoCycle, "Auto-cycle (5s)");
        AppendMenuW(menu, MF_STRING | (s.ConnectionBar ? MF_CHECKED : 0), CmdConnectionBar, "Show connection bar");
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING | (s.Startup ? MF_CHECKED : 0), CmdStartup, "Start with Windows");
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, CmdCheckUpdates, "Check for updates…");
        AppendMenuW(menu, MF_STRING, CmdDashboard, "Open dashboard");
        AppendMenuW(menu, MF_STRING, CmdExit, "Exit");

        GetCursorPos(out var p);
        SetForegroundWindow(_hwnd);   // so the menu dismisses when clicking elsewhere
        uint id = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, 0, _hwnd, IntPtr.Zero);
        PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        if (id != 0) _onCommand((int)id);
    }

    /// <summary>Pop a balloon/toast from the tray icon. Best-effort; the info flags are cleared right
    /// after so a later NIM_MODIFY (e.g. a tip change) doesn't re-show a stale balloon.</summary>
    public void ShowBalloon(string title, string text)
    {
        if (_hwnd == IntPtr.Zero) return;
        _data.uFlags = NIF_INFO;
        _data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        _data.szInfo = text.Length > 255 ? text[..255] : text;
        _data.dwInfoFlags = NIIF_INFO;
        _data.uTimeoutOrVersion = 10000;
        Shell_NotifyIconW(NIM_MODIFY, ref _data);
        _data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;   // restore for subsequent modifies
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            Shell_NotifyIconW(NIM_DELETE, ref _data);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
