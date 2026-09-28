using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Rdpeek.Agent;

/// <summary>Live serve state, surfaced in the tray tooltip and (on change) a balloon.</summary>
internal enum AgentStatus { Waiting, Connected, Disconnected }

/// <summary>
/// A pure-Win32 system-tray presence for <c>rdpeek-agent serve</c> — no WinForms/WPF, no project
/// reference to the Companion (adapted from <c>Rdpeek.Companion.WinUI/TrayIcon.cs</c>). It hosts a
/// message-only window, adds a <c>Shell_NotifyIcon</c>, and shows a classic popup menu on right-click.
/// The serve loop runs on a background thread and reports status via <see cref="SetStatus"/>; that
/// call marshals onto the tray thread through a posted message, so all <c>Shell_NotifyIcon</c> calls
/// happen where the icon lives. <see cref="RunServe"/> owns the main-thread message loop.
/// </summary>
internal sealed class AgentTray : IDisposable
{
    private const int CmdStatus = 1, CmdOpenLog = 2, CmdExit = 3;

    private const int WM_TRAY = 0x8000 + 1;     // WM_APP + 1  (tray callback)
    private const int WM_STATUS = 0x8000 + 2;   // WM_APP + 2  (marshal a SetStatus onto the tray thread)
    private const int WM_QUITREQ = 0x8000 + 3;  // WM_APP + 3  (ask the tray thread to quit its message loop)
    private const int WM_RBUTTONUP = 0x0205, WM_LBUTTONDBLCLK = 0x0203, WM_CONTEXTMENU = 0x007B, WM_NULL = 0;
    private const int NIN_BALLOONUSERCLICK = 0x0405;
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const int NIIF_INFO = 1;
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_GRAYED = 1;
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;
    private const int SW_HIDE = 0;

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
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private readonly Action<int> _onCommand;
    private readonly WndProc _proc;   // kept alive against GC (it's an unmanaged callback)
    private IntPtr _hwnd;
    private NOTIFYICONDATAW _data;

    private readonly object _statusGate = new();
    private AgentStatus _pendingStatus = AgentStatus.Waiting;
    private string _pendingTip = "RDPeek agent — starting…";
    private AgentStatus _shownStatus = (AgentStatus)(-1);   // force the first apply to look like a change

    private AgentTray(Action<int> onCommand)
    {
        _onCommand = onCommand;
        _proc = WndProcImpl;

        var hinst = GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hinst,
            lpszClassName = "RdpeekAgentTrayWnd",
        };
        RegisterClassExW(ref wc);
        _hwnd = CreateWindowExW(0, "RdpeekAgentTrayWnd", "", 0, 0, 0, 0, 0, new IntPtr(-3) /*HWND_MESSAGE*/, IntPtr.Zero, hinst, IntPtr.Zero);

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
            szTip = _pendingTip,
        };
        Shell_NotifyIconW(NIM_ADD, ref _data);
    }

    /// <summary>
    /// Hides the console window (unless already absent) and runs the serve loop under a tray icon:
    /// <paramref name="serve"/> runs on a background thread and receives a status callback wired to the
    /// tray; the tray's message loop owns this (the main) thread until the serve loop returns or the user
    /// picks Exit. Returns the serve loop's exit code.
    /// </summary>
    public static int RunServe(Func<Action<AgentStatus, string>, CancellationToken, int> serve)
    {
        // Hide the cmd window the scheduled task spawns; a null handle (no console) is a no-op.
        var console = GetConsoleWindow();
        if (console != IntPtr.Zero) ShowWindow(console, SW_HIDE);

        using var cts = new CancellationTokenSource();
        AgentTray? tray = null;
        int rc = 0;

        tray = new AgentTray(cmd =>
        {
            switch (cmd)
            {
                case CmdStatus:
                    tray!.RepeatStatusBalloon();
                    break;
                case CmdOpenLog:
                    try { Process.Start(new ProcessStartInfo(Logger.LogPath) { UseShellExecute = true }); }
                    catch (Exception ex) { Logger.Log($"tray: open log failed: {ex.Message}"); }
                    break;
                case CmdExit:
                    cts.Cancel();          // ask the serve loop to stop…
                    PostQuitMessage(0);    // …and break our own message loop
                    break;
            }
        });

        var worker = new Thread(() =>
        {
            try { rc = serve(tray!.SetStatus, cts.Token); }
            catch (Exception ex) { Logger.Log($"serve worker crashed: {ex}"); rc = 1; }
            finally { tray!.RequestQuit(); }   // serve ended on its own → quit the loop from the pump thread
        })
        { IsBackground = true, Name = "rdpeek-serve" };
        worker.Start();

        // Main-thread message pump: keeps the tray icon and its menu responsive.
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        cts.Cancel();
        worker.Join(TimeSpan.FromSeconds(3));
        tray.Dispose();
        return rc;
    }

    /// <summary>Thread-safe: called from the serve worker. Marshals the update onto the tray thread.</summary>
    public void SetStatus(AgentStatus status, string tooltip)
    {
        lock (_statusGate) { _pendingStatus = status; _pendingTip = tooltip; }
        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_STATUS, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Thread-safe: ask the tray's message loop to quit — used when the serve worker ends on
    /// its own. PostQuitMessage must run on the pump thread, so bounce it through the window.</summary>
    public void RequestQuit()
    {
        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_QUITREQ, IntPtr.Zero, IntPtr.Zero);
    }

    private IntPtr WndProcImpl(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == WM_TRAY)
        {
            int mouse = (int)(l.ToInt64() & 0xFFFF);
            if (mouse == WM_RBUTTONUP || mouse == WM_CONTEXTMENU) ShowMenu();
            else if (mouse == WM_LBUTTONDBLCLK) _onCommand(CmdStatus);
            else if (mouse == NIN_BALLOONUSERCLICK) _onCommand(CmdStatus);
            return IntPtr.Zero;
        }
        if (msg == WM_STATUS) { ApplyStatus(); return IntPtr.Zero; }
        if (msg == WM_QUITREQ) { PostQuitMessage(0); return IntPtr.Zero; }   // on the pump thread — correct
        return DefWindowProcW(h, msg, w, l);
    }

    // Runs on the tray thread. Updates the tooltip; on a status transition, also pops a balloon.
    private void ApplyStatus()
    {
        AgentStatus status; string tip;
        lock (_statusGate) { status = _pendingStatus; tip = _pendingTip; }

        _data.uFlags = NIF_TIP;
        _data.szTip = tip.Length > 127 ? tip[..127] : tip;
        Shell_NotifyIconW(NIM_MODIFY, ref _data);
        _data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;

        if (status != _shownStatus)
        {
            _shownStatus = status;
            if (status == AgentStatus.Connected) ShowBalloon("RDPeek agent", "Client connected — serving diagnostics.");
            else if (status == AgentStatus.Disconnected) ShowBalloon("RDPeek agent", "Client disconnected — waiting to reconnect.");
        }
    }

    private void RepeatStatusBalloon()
    {
        string tip;
        lock (_statusGate) tip = _pendingTip;
        ShowBalloon("RDPeek agent", tip);
    }

    private void ShowMenu()
    {
        AgentStatus status; string tip;
        lock (_statusGate) { status = _pendingStatus; tip = _pendingTip; }
        var label = status switch
        {
            AgentStatus.Connected => "Status: connected",
            AgentStatus.Disconnected => "Status: disconnected — reconnecting",
            _ => "Status: waiting for client",
        };

        var menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING | MF_GRAYED, CmdStatus + 100, label);   // header, non-clickable
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, CmdStatus, "Show status");
        AppendMenuW(menu, MF_STRING, CmdOpenLog, "Open log");
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, CmdExit, "Exit (stop serving)");

        GetCursorPos(out var p);
        SetForegroundWindow(_hwnd);   // so the menu dismisses when clicking elsewhere
        uint id = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, 0, _hwnd, IntPtr.Zero);
        PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        if (id is CmdStatus or CmdOpenLog or CmdExit) _onCommand((int)id);
    }

    /// <summary>Pop a balloon from the tray icon. Info flags are cleared right after so a later
    /// tooltip-only NIM_MODIFY doesn't re-show a stale balloon.</summary>
    public void ShowBalloon(string title, string text)
    {
        if (_hwnd == IntPtr.Zero) return;
        _data.uFlags = NIF_INFO;
        _data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        _data.szInfo = text.Length > 255 ? text[..255] : text;
        _data.dwInfoFlags = NIIF_INFO;
        _data.uTimeoutOrVersion = 10000;
        Shell_NotifyIconW(NIM_MODIFY, ref _data);
        _data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
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
