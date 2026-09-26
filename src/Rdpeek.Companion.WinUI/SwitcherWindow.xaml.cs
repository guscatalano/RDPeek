using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace Rdpeek.Companion.WinUI;

/// <summary>
/// A thin, borderless, always-on-top sidebar docked to the left edge of the primary monitor. It lists
/// every open RDP connection (the same live collection the dashboard shows) and, on click, brings that
/// mstsc window to the foreground via the in-process window plugin — an overlay switcher across all
/// sessions. Shares the dashboard's MainViewModel so selection and state stay in sync.
/// </summary>
public sealed partial class SwitcherWindow : Window
{
    public MainViewModel Vm { get; }

    /// <summary>Raised when the user clicks Hide; the owner hides (not closes) the window so the edge
    /// trigger can bring the same instance back.</summary>
    public event Action? HideRequested;

    public SwitcherWindow(MainViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        Title = "RDP switcher";

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);   // chromeless overlay strip
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }

        DockTo(false);
        Activated += OnActivated;
    }

    private const int PanelWidth = 250;

    /// <summary>Dock the panel to the left (default) or right edge of the primary work area.</summary>
    public void DockTo(bool right)
    {
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int x = right ? work.X + work.Width - PanelWidth : work.X;
        AppWindow.MoveAndResize(new RectInt32(x, work.Y, PanelWidth, work.Height));
    }

    private bool _wasActivated;

    /// <summary>Auto-hide when the panel loses focus (clicking a session, the dashboard, or anywhere
    /// else). Guarded by _wasActivated so the initial show doesn't hide it before it's ever focused.</summary>
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated) { _wasActivated = true; return; }
        if (_wasActivated) HideRequested?.Invoke();
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ConnectionRow row)
            Vm.ActivateConnectionCommand.Execute(row);
    }

    private void OnLocalDesktop(object sender, RoutedEventArgs e)
    {
        Vm.ShowLocalDesktopCommand.Execute(null);
        HideRequested?.Invoke();
    }

    private void OnHide(object sender, RoutedEventArgs e) => HideRequested?.Invoke();

    /// <summary>Slide away when the pointer leaves the panel — a peek panel disappears when you move off it.</summary>
    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => HideRequested?.Invoke();
}
