using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

        // Dock to the left edge of the primary work area (full height, minus the taskbar).
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new RectInt32(work.X, work.Y, 250, work.Height));

        Activated += OnActivated;
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

    private void OnHide(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
}
