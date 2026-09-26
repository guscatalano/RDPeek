using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace Rdpeek.Companion.WinUI;

/// <summary>
/// A small always-on-top nub docked to the left edge at vertical centre — a visible hint for where the
/// switcher lives. Hovering (or clicking) it asks the owner to reveal the sidebar. It's shown whenever
/// the sidebar is hidden and hidden while the sidebar is up.
/// </summary>
public sealed partial class EdgeHandleWindow : Window
{
    public event Action? RevealRequested;

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

        const int w = 14, h = 120;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new RectInt32(work.X, work.Y + (work.Height - h) / 2, w, h));
    }

    private void OnReveal(object sender, PointerRoutedEventArgs e) => RevealRequested?.Invoke();
}
