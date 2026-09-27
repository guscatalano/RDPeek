using System.Runtime.InteropServices;

namespace Rdpeek.Companion.WinUI;

/// <summary>
/// Global Ctrl+Alt+Left / Right for switching sessions. Uses a low-level keyboard hook
/// (WH_KEYBOARD_LL) rather than RegisterHotKey: a focused or fullscreen RDP session grabs the keyboard
/// and RegisterHotKey hotkeys never fire, whereas the LL hook can intercept the combo first and swallow
/// it so it doesn't reach the remote. The hook lives on a dedicated thread that pumps messages (the OS
/// dispatches LL-hook callbacks through that thread's queue); it only acts while <see cref="Enabled"/>.
/// </summary>
public sealed class HotkeyListener : IDisposable
{
    public const int NextId = 1;
    public const int PrevId = 2;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_QUIT = 0x0012;
    private const int VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObj, int idChild, uint thread, uint time);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SetWindowsHookExW(int idHook, HookProc proc, IntPtr hmod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc cb, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG m, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? n);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private HookProc? _proc;            // kept alive for the hook
    private WinEventProc? _winProc;     // kept alive for the foreground hook
    private IntPtr _hook;
    private IntPtr _winHook;
    private uint _tid;
    private volatile bool _enabled;
    private int _lastFire;

    /// <summary>Raised on the hook thread with the hotkey id (Next/Prev). Handlers marshal to the UI.</summary>
    public event Action<int>? Pressed;

    public HotkeyListener()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "rdpeek-hotkeys" };
        _thread.Start();
        _ready.Wait(2000);
    }

    public void Enable() => _enabled = true;
    public void Disable() => _enabled = false;

    private void Run()
    {
        _tid = GetCurrentThreadId();
        _proc = HookCallback;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);

        // When the foreground window changes (e.g. an mstsc session gains focus and installs its own
        // keyboard hook), re-install ours so it's the most-recent — the OS calls the newest LL hook
        // first, giving us the keys before the RDP client swallows them in fullscreen.
        _winProc = OnForeground;
        _winHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _ready.Set();

        // Pump so the OS can dispatch LL-hook and WinEvent callbacks on this thread.
        while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }

        if (_winHook != IntPtr.Zero) UnhookWinEvent(_winHook);
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    private void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObj, int idChild, uint thread, uint time)
    {
        if (!_enabled) return;
        // Re-install the keyboard hook so it becomes the newest in the chain (called first).
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc!, GetModuleHandleW(null), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _enabled)
        {
            int msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)   // arrows arrive as SYSKEYDOWN while Alt is held
            {
                int vk = Marshal.ReadInt32(lParam);          // KBDLLHOOKSTRUCT.vkCode is the first field
                if ((vk == VK_LEFT || vk == VK_RIGHT)
                    && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
                    && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
                {
                    int now = Environment.TickCount;
                    if (now - _lastFire > 250) { _lastFire = now; Pressed?.Invoke(vk == VK_LEFT ? PrevId : NextId); }
                    return (IntPtr)1;   // swallow it so the RDP session doesn't also receive the arrow
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_tid != 0) PostThreadMessageW(_tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _ready.Dispose();
    }
}
