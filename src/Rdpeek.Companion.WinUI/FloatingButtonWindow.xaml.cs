using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace Rdpeek.Companion.WinUI;

/// <summary>
/// A round, always-on-top floating button you can drag anywhere — an alternative to the edge-docked
/// nub. Click it (without dragging) to reveal the switcher; press and drag to reposition. The window
/// is clipped to a circle with SetWindowRgn so the square corners don't show.
/// </summary>
public sealed partial class FloatingButtonWindow : Window
{
    public event Action? RevealRequested;

    private const int Size = 52;

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePolygonRgn(POINT[] pts, int count, int mode);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private bool _pressed, _moved;
    private POINT _cursorStart;
    private PointInt32 _winStart;

    public FloatingButtonWindow()
    {
        InitializeComponent();
        Title = "RDP switcher button";

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsAlwaysOnTop = true;
            p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false;
        }

        // Default spot: left, a little in from the edge, vertically centred.
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new RectInt32(work.X + 24, work.Y + (work.Height - Size) / 2, Size, Size));

        // Clip the window to the same hexagon the XAML draws (flat top/bottom, points at left/right).
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        int q = Size / 4, half = Size / 2;
        var hex = new POINT[]
        {
            new() { X = q, Y = 0 }, new() { X = Size - q, Y = 0 }, new() { X = Size, Y = half },
            new() { X = Size - q, Y = Size }, new() { X = q, Y = Size }, new() { X = 0, Y = half },
        };
        SetWindowRgn(hwnd, CreatePolygonRgn(hex, hex.Length, 2 /*WINDING*/), true);
    }

    private void OnDown(object sender, PointerRoutedEventArgs e)
    {
        _pressed = true; _moved = false;
        GetCursorPos(out _cursorStart);
        _winStart = AppWindow.Position;
        Root.CapturePointer(e.Pointer);
    }

    private void OnMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        GetCursorPos(out var cur);
        int dx = cur.X - _cursorStart.X, dy = cur.Y - _cursorStart.Y;
        if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3) _moved = true;
        AppWindow.Move(new PointInt32(_winStart.X + dx, _winStart.Y + dy));
    }

    private void OnUp(object sender, PointerRoutedEventArgs e)
    {
        Root.ReleasePointerCapture(e.Pointer);
        bool wasClick = _pressed && !_moved;
        _pressed = false;
        if (wasClick) RevealRequested?.Invoke();   // a tap (not a drag) opens the switcher
    }
}
