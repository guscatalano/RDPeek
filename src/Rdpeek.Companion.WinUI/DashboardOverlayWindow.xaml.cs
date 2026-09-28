using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Rdpeek.Companion.WinUI;

/// <summary>
/// A translucent, always-on-top HUD that floats the dashboard's per-connection health + live metrics
/// over whatever session is in front — a glanceable overlay you toggle from the switcher, without
/// bringing up the full Companion window. Shares the dashboard's MainViewModel so it's always live.
/// </summary>
public sealed partial class DashboardOverlayWindow : Window
{
    public MainViewModel Vm { get; }

    public event Action? CloseRequested;

    public DashboardOverlayWindow(MainViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        Title = "RDPeek dashboard overlay";

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsAlwaysOnTop = true;      // stays above the (fullscreen) RDP session — the point of a HUD
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }
        AppWindow.IsShownInSwitchers = false;   // out of taskbar / Alt+Tab

        // Translucent backdrop so the session shows through — falls back to the solid Root background
        // if acrylic isn't available.
        try { SystemBackdrop = new DesktopAcrylicBackdrop(); } catch { }
    }

    private const int OverlayWidth = 460;

    /// <summary>Place the HUD near the top-centre of the primary work area, tall enough to be useful but
    /// never taller than the screen.</summary>
    public void PositionTopCentre()
    {
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int h = Math.Min(work.Height - 80, 560);
        int x = work.X + (work.Width - OverlayWidth) / 2;
        int y = work.Y + 40;
        AppWindow.MoveAndResize(new RectInt32(x, y, OverlayWidth, h));
    }

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
