using System.Runtime.InteropServices;

namespace Rdpeek.Companion.WinUI;

/// <summary>
/// Registers a pair of global (system-wide) hotkeys on a dedicated thread and raises <see cref="Pressed"/>
/// when one fires. Uses thread-scoped RegisterHotKey (hWnd = 0), so WM_HOTKEY lands in this thread's own
/// message queue — no window and no WinUI WndProc subclassing needed. Register/unregister are marshalled
/// onto the listener thread (RegisterHotKey is thread-affine: WM_HOTKEY goes to the registering thread).
/// </summary>
public sealed class HotkeyListener : IDisposable
{
    public const int NextId = 1;
    public const int PrevId = 2;

    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
    private const uint VK_RIGHT = 0x27, VK_LEFT = 0x25;
    private const uint WM_HOTKEY = 0x0312, WM_QUIT = 0x0012;
    private const uint WM_APP_REGISTER = 0x8001, WM_APP_UNREGISTER = 0x8002;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG m, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _tid;

    /// <summary>Raised on the listener thread with the hotkey id (<see cref="NextId"/>/<see cref="PrevId"/>).
    /// Handlers must marshal to the UI thread themselves.</summary>
    public event Action<int>? Pressed;

    public HotkeyListener()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "rdpeek-hotkeys" };
        _thread.Start();
        _ready.Wait(2000);
    }

    public void Enable() => PostThreadMessageW(_tid, WM_APP_REGISTER, IntPtr.Zero, IntPtr.Zero);
    public void Disable() => PostThreadMessageW(_tid, WM_APP_UNREGISTER, IntPtr.Zero, IntPtr.Zero);

    private void Run()
    {
        _tid = GetCurrentThreadId();
        _ready.Set();
        while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            switch (m.message)
            {
                case WM_HOTKEY:
                    Pressed?.Invoke(m.w.ToInt32());   // wParam = the hotkey id
                    break;
                case WM_APP_REGISTER:
                    RegisterHotKey(IntPtr.Zero, NextId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_RIGHT);
                    RegisterHotKey(IntPtr.Zero, PrevId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_LEFT);
                    break;
                case WM_APP_UNREGISTER:
                    UnregisterHotKey(IntPtr.Zero, NextId);
                    UnregisterHotKey(IntPtr.Zero, PrevId);
                    break;
            }
        }
    }

    public void Dispose()
    {
        if (_tid != 0)
        {
            Disable();
            PostThreadMessageW(_tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        _thread.Join(1000);
        _ready.Dispose();
    }
}
