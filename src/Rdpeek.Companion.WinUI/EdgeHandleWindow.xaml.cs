using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace Rdpeek.Companion.WinUI;

/// <summary>
/// A slim always-on-top sliver docked to the left edge at vertical centre — a subtle hint for where the
/// switcher lives. Hovering (or clicking) it asks the owner to reveal the sidebar. Shown while the
/// sidebar is hidden, hidden while it's up.
///
/// Windows floors a window's width at its minimum tracking size (~136px), so a plain MoveAndResize to
/// 10px is ignored; we subclass the WndProc to answer WM_GETMINMAXINFO with a tiny minimum, then size it.
/// </summary>
public sealed partial class EdgeHandleWindow : Window
{
    public event Action? RevealRequested;

    private const int GWLP_WNDPROC = -4;
    private const uint WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] private static extern IntPtr CallWindowProcW(IntPtr prev, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private WndProc? _proc;      // kept alive for the lifetime of the window
    private IntPtr _prevProc;

    public EdgeHandleWindow()
    {
        InitializeComponent();
        Title = "RDP switcher handle";

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }
        AppWindow.IsShownInSwitchers = false;   // keep it out of the taskbar / Alt+Tab

        // Lift the OS minimum tracking size so the sliver can be genuinely thin.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _proc = HandleWndProc;
        _prevProc = SetWindowLongPtrW(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_proc));

        DockTo(false);
    }

    private const int NubW = 10, NubH = 72;   // subtle sliver

    /// <summary>Dock the nub to the left (default) or right edge, mid-screen, with its rounded corner
    /// facing inward.</summary>
    public void DockTo(bool right)
    {
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int x = right ? work.X + work.Width - NubW : work.X;
        AppWindow.MoveAndResize(new RectInt32(x, work.Y + (work.Height - NubH) / 2, NubW, NubH));
        Nub.CornerRadius = right ? new CornerRadius(4, 0, 0, 4) : new CornerRadius(0, 4, 4, 0);
    }

    private IntPtr HandleWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMinTrackSize.X = 2;
            mmi.ptMinTrackSize.Y = 2;
            Marshal.StructureToPtr(mmi, lParam, false);
            return IntPtr.Zero;
        }
        return CallWindowProcW(_prevProc, hWnd, msg, wParam, lParam);
    }

    private void OnReveal(object sender, PointerRoutedEventArgs e) => RevealRequested?.Invoke();
}
