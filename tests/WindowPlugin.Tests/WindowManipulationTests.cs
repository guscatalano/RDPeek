using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace WindowPlugin.Tests;

// End-to-end test of the native in-process window plugin (src/Rdpeek.WindowPlugin). Rather than mock
// the Win32 layer, this stands up the real thing in the test process:
//   1. create a genuine top-level window of class "TscShellContainerClass" (what the plugin hunts for),
//      on its own thread with a message pump so cross-thread WM_SETTEXT / ShowWindow land;
//   2. LoadLibrary the built DLL and call its IClassFactory to create the plugin object, which starts
//      the same UI + pipe + attach threads mstsc would drive — the attach thread binds to our window;
//   3. send the exact newline-delimited commands the Companion sends over \\.\pipe\rdpeek-window-<pid>
//      and assert the window actually changed (title, minimized state, always-on-top, overlay).
// This is the real "manipulating the window" contract, exercised without an RDP session.

internal static class Native
{
    public const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const int SW_SHOWNA = 8;
    public const uint WM_QUIT = 0x0012;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOPMOST = 0x00000008;

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(int exStyle, string cls, string title, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] public static extern bool PostThreadMessageW(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] public static extern IntPtr GetProcAddress(IntPtr mod, string name);

    // DllGetClassObject(REFCLSID, REFIID, void**) and IClassFactory::CreateInstance via the raw vtable —
    // no COM apartment/registration needed for an in-proc call into a DLL we loaded ourselves.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int DllGetClassObject(ref Guid clsid, ref Guid iid, out IntPtr ppv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int CreateInstance(IntPtr self, IntPtr outer, ref Guid iid, out IntPtr ppv);
}

/// <summary>Owns a real TscShellContainerClass window + the loaded/activated plugin for the class.
/// Created once, torn down once; the plugin's pipe is a single per-process instance.</summary>
public sealed class WindowPluginFixture : IDisposable
{
    private const string MstscClass = "TscShellContainerClass";
    private static readonly Guid CLSID_WindowPlugin = new("7B6D1E44-9C1A-4C7E-9E2B-11A0C0FFEE03");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IClassFactory = new("00000001-0000-0000-C000-000000000046");

    public const string InitialTitle = "TEST mstsc host window";
    public IntPtr Hwnd { get; private set; }
    public string PipeName => $"rdpeek-window-{Environment.ProcessId}";

    private readonly Thread _uiThread;
    private uint _uiThreadId;
    private Native.WndProc? _wndProc;   // kept alive for the window class
    private readonly ManualResetEventSlim _created = new();

    public WindowPluginFixture()
    {
        _uiThread = new Thread(RunWindow) { IsBackground = true, Name = "tsc-host" };
        _uiThread.Start();
        Assert.True(_created.Wait(TimeSpan.FromSeconds(10)), "host window was not created");
        Assert.NotEqual(IntPtr.Zero, Hwnd);

        LoadAndActivatePlugin();

        // The plugin's attach thread must find our window and its pipe must be up. Prove both at once:
        // a probe tag round-trips only when attach + pipe are ready. Then restore the title.
        Assert.True(WaitFor(() => { TrySend("title __ready__"); return Title().Contains("__ready__"); }, 15000),
            "plugin did not attach to the host window / serve its pipe in time");
        Send("title");
        Assert.True(WaitFor(() => Title() == InitialTitle, 5000), "title did not restore after readiness probe");
    }

    private void RunWindow()
    {
        _uiThreadId = Native.GetCurrentThreadId();
        _wndProc = Native.DefWindowProcW;
        var wc = new Native.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = Native.GetModuleHandleW(null),
            lpszClassName = MstscClass,
        };
        Native.RegisterClassExW(ref wc);   // ignore "already registered" on a re-run within a process
        Hwnd = Native.CreateWindowExW(0, MstscClass, InitialTitle, Native.WS_OVERLAPPEDWINDOW,
            100, 100, 480, 320, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        Native.ShowWindow(Hwnd, Native.SW_SHOWNA);   // visible: FindMstscWindow requires IsWindowVisible
        _created.Set();

        // Pump: the plugin drives this window from its own threads, so WM_SETTEXT/ShowWindow marshal here.
        while (Native.GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref m);
            Native.DispatchMessageW(ref m);
        }
    }

    private static void LoadAndActivatePlugin()
    {
        string dll = FindPluginDll();
        IntPtr mod = Native.LoadLibraryW(dll);
        Assert.True(mod != IntPtr.Zero, $"LoadLibrary failed for {dll} (err {Marshal.GetLastWin32Error()})");

        IntPtr proc = Native.GetProcAddress(mod, "DllGetClassObject");
        Assert.True(proc != IntPtr.Zero, "DllGetClassObject export not found");
        var dllGetClassObject = Marshal.GetDelegateForFunctionPointer<Native.DllGetClassObject>(proc);

        var clsid = CLSID_WindowPlugin;
        var iidFactory = IID_IClassFactory;
        Assert.Equal(0, dllGetClassObject(ref clsid, ref iidFactory, out IntPtr pFactory));
        Assert.NotEqual(IntPtr.Zero, pFactory);

        // IClassFactory vtable slot 3 = CreateInstance. Creating an instance is what starts the plugin's
        // background threads (UI/pipe/attach) — the same call CoCreateInstance makes inside mstsc.
        IntPtr vtbl = Marshal.ReadIntPtr(pFactory);
        IntPtr createPtr = Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size);
        var createInstance = Marshal.GetDelegateForFunctionPointer<Native.CreateInstance>(createPtr);
        var iidUnknown = IID_IUnknown;
        Assert.Equal(0, createInstance(pFactory, IntPtr.Zero, ref iidUnknown, out IntPtr pObj));
        Assert.NotEqual(IntPtr.Zero, pObj);
        // We intentionally keep pObj/pFactory alive: the plugin's threads outlive the COM object anyway
        // (DllCanUnloadNow returns S_FALSE), and the test process exits at the end of the run.
    }

    private static string FindPluginDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Rdpeek.WindowPlugin", "bin", "rdpeek-window-plugin.dll");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(
            "rdpeek-window-plugin.dll not found under any ancestor's src/Rdpeek.WindowPlugin/bin — run build.cmd there.");
    }

    // ── driving the plugin ────────────────────────────────────────────────
    public void Send(string command)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
        pipe.Connect(2000);
        using var w = new StreamWriter(pipe) { AutoFlush = true };
        w.WriteLine(command);
    }

    /// <summary>Best-effort send used while waiting for the pipe to come up (swallows connect failures).</summary>
    public void TrySend(string command) { try { Send(command); } catch { /* pipe not ready yet */ } }

    public string Title()
    {
        var sb = new StringBuilder(512);
        Native.GetWindowTextW(Hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public bool IsMinimized() => Native.IsIconic(Hwnd);

    public bool IsTopmost() =>
        ((long)Native.GetWindowLongPtr(Hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0;

    public static bool OverlayExists() =>
        Native.FindWindowExW(IntPtr.Zero, IntPtr.Zero, "RdpeekOverlay", null) != IntPtr.Zero;

    public static bool WaitFor(Func<bool> condition, int timeoutMs = 5000, int stepMs = 100)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (condition()) return true; Thread.Sleep(stepMs); }
        return condition();
    }

    public void Dispose()
    {
        if (_uiThreadId != 0) Native.PostThreadMessageW(_uiThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _uiThread.Join(TimeSpan.FromSeconds(3));
        _created.Dispose();
    }
}

public sealed class WindowManipulationTests : IClassFixture<WindowPluginFixture>
{
    private readonly WindowPluginFixture _f;
    public WindowManipulationTests(WindowPluginFixture f) => _f = f;

    [Fact]
    public void Title_command_appends_to_the_window_title()
    {
        _f.Send("title RDPEEK-TAG");
        Assert.True(WindowPluginFixture.WaitFor(() => _f.Title().Contains("RDPEEK-TAG")),
            $"title never showed the tag (was '{_f.Title()}')");
        Assert.StartsWith(WindowPluginFixture.InitialTitle, _f.Title());   // original kept, tag appended
    }

    [Fact]
    public void Empty_title_command_restores_the_original_title()
    {
        _f.Send("title TEMP-XYZ");
        Assert.True(WindowPluginFixture.WaitFor(() => _f.Title().Contains("TEMP-XYZ")));
        _f.Send("title");
        Assert.True(WindowPluginFixture.WaitFor(() => _f.Title() == WindowPluginFixture.InitialTitle),
            $"title did not restore to the original (was '{_f.Title()}')");
    }

    [Fact]
    public void Show_min_and_restore_change_the_minimized_state()
    {
        _f.Send("show min");
        Assert.True(WindowPluginFixture.WaitFor(() => _f.IsMinimized()), "window did not minimize");
        _f.Send("show restore");
        Assert.True(WindowPluginFixture.WaitFor(() => !_f.IsMinimized()), "window did not restore from minimized");
    }

    [Fact]
    public void Topmost_on_and_off_toggle_the_always_on_top_style()
    {
        _f.Send("topmost on");
        Assert.True(WindowPluginFixture.WaitFor(() => _f.IsTopmost()), "WS_EX_TOPMOST was not set");
        _f.Send("topmost off");
        Assert.True(WindowPluginFixture.WaitFor(() => !_f.IsTopmost()), "WS_EX_TOPMOST was not cleared");
    }

    [Fact]
    public void Overlay_hud_window_is_created()
    {
        // The plugin creates a topmost "RdpeekOverlay" HUD; asking for it must not throw and it exists.
        _f.Send("overlay integration-test overlay");
        Assert.True(WindowPluginFixture.WaitFor(WindowPluginFixture.OverlayExists),
            "the RdpeekOverlay HUD window was never created");
    }
}
