using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Rdpeek.Companion.WinUI;

/// <summary>
/// A fullscreen, click-through, always-on-top black overlay used for a quick "dip to black" when
/// switching sessions. We can't crossfade the RDP surfaces themselves, so we fade this to opaque,
/// perform the switch underneath, then fade it back out. It never takes focus (WS_EX_NOACTIVATE) and
/// passes input through (WS_EX_TRANSPARENT); alpha is driven with SetLayeredWindowAttributes.
/// </summary>
public sealed partial class FadeWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020, WS_EX_NOACTIVATE = 0x08000000;
    private const uint LWA_ALPHA = 0x2;
    private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLongW(IntPtr h, int i);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLongW(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const int MaxAlpha = 235, Step = 55;
    private readonly IntPtr _hwnd;
    private readonly DispatcherQueueTimer _timer;
    private Action? _atPeak;
    private int _alpha;
    private bool _rising, _busy;

    public FadeWindow()
    {
        InitializeComponent();
        Title = "RDP switch fade";

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsAlwaysOnTop = true;
            p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false;
        }
        AppWindow.IsShownInSwitchers = false;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        int ex = GetWindowLongW(_hwnd, GWL_EXSTYLE);
        SetWindowLongW(_hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        SetLayeredWindowAttributes(_hwnd, 0, 0, LWA_ALPHA);

        // Cover the whole primary monitor (including the taskbar area).
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).OuterBounds;
        AppWindow.MoveAndResize(new RectInt32(area.X, area.Y, area.Width, area.Height));
        AppWindow.Hide();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += (_, _) => Step_();
    }

    /// <summary>Dip to black, run <paramref name="atPeak"/> while opaque, then fade back in.</summary>
    public void RunFade(Action atPeak)
    {
        if (_busy) { atPeak(); return; }   // already mid-transition — just do the switch
        _busy = true;
        _atPeak = atPeak;
        _alpha = 0;
        _rising = true;
        SetLayeredWindowAttributes(_hwnd, 0, 0, LWA_ALPHA);
        Raise();
        AppWindow.Show(false);
        _timer.Start();
    }

    private void Raise() => SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);

    private void Step_()
    {
        if (_rising)
        {
            _alpha += Step;
            if (_alpha >= MaxAlpha)
            {
                _alpha = MaxAlpha;
                SetLayeredWindowAttributes(_hwnd, 0, (byte)_alpha, LWA_ALPHA);
                _atPeak?.Invoke();         // switch the session while the screen is black
                _atPeak = null;
                Raise();                   // keep the overlay above the newly-foregrounded session
                _rising = false;
                return;
            }
        }
        else
        {
            _alpha -= Step;
            if (_alpha <= 0)
            {
                _timer.Stop();
                AppWindow.Hide();
                _busy = false;
                return;
            }
        }
        SetLayeredWindowAttributes(_hwnd, 0, (byte)_alpha, LWA_ALPHA);
    }
}
