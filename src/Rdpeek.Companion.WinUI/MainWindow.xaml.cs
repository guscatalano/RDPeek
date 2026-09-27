using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Rdpeek.Companion.WinUI;

public sealed partial class MainWindow : Window
{
    public MainViewModel Vm { get; }

    public MainWindow()
    {
        Vm = new MainViewModel(DispatcherQueue);
        InitializeComponent();
        Title = "RDPeek Companion";
        try { AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "rdpeek.ico")); } catch { }

        // Reveal the switcher (focused) when the cursor is shoved into the left edge (mid-screen). It
        // auto-hides on blur, so it must take focus to be dismissable that way. Poll-thread callback
        // marshals onto the UI thread.
        _edge = new EdgeTrigger(() => DispatcherQueue.TryEnqueue(() => ShowSwitcher()));
        Closed += (_, _) => _edge?.Dispose();

        // A visible nub at the edge shows where to aim; hovering it reveals the sidebar.
        _handle = new EdgeHandleWindow();
        _handle.RevealRequested += () => DispatcherQueue.TryEnqueue(() => ShowSwitcher());

        // Alternative: a draggable floating circle; click it to reveal the sidebar.
        _floating = new FloatingButtonWindow();
        _floating.RevealRequested += () => DispatcherQueue.TryEnqueue(() => ShowSwitcher());

        ShowActiveHandle();
        Closed += (_, _) => { try { _handle?.Close(); _floating?.Close(); } catch { } };

        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.DockRight)) ApplyDock();
            else if (e.PropertyName == nameof(MainViewModel.UseFloatingButton)) { if (!_switcherVisible) ShowActiveHandle(); }
        };

        // System-tray icon: the primary way to drive the switcher without the dashboard open.
        _tray = new TrayIcon(
            () => new TrayState(Vm.DockRight, Vm.AutoCycle, Vm.ShowConnectionBar, Vm.UseFloatingButton, Vm.HotkeysEnabled),
            id => DispatcherQueue.TryEnqueue(() => OnTrayCommand(id)));

        // Closing the dashboard hides it to the tray rather than quitting; Exit (tray) really quits.
        AppWindow.Closing += (_, e) => { if (!_exiting) { e.Cancel = true; AppWindow.Hide(); } };
    }

    private void OnNavChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "dash";
        ConnectView.Visibility = tag == "connect" ? Visibility.Visible : Visibility.Collapsed;
        DashView.Visibility = tag == "dash" ? Visibility.Visible : Visibility.Collapsed;
        NetView.Visibility = tag == "net" ? Visibility.Visible : Visibility.Collapsed;
        SessView.Visibility = tag == "sess" ? Visibility.Visible : Visibility.Collapsed;
        SvcView.Visibility = tag == "svc" ? Visibility.Visible : Visibility.Collapsed;
        ChanView.Visibility = tag == "chan" ? Visibility.Visible : Visibility.Collapsed;
        DvcView.Visibility = tag == "dvc" ? Visibility.Visible : Visibility.Collapsed;
        LinkView.Visibility = tag == "link" ? Visibility.Visible : Visibility.Collapsed;
        FilesView.Visibility = tag == "files" ? Visibility.Visible : Visibility.Collapsed;
        FramesView.Visibility = tag == "frames" ? Visibility.Visible : Visibility.Collapsed;
        DiagView.Visibility = tag == "diag" ? Visibility.Visible : Visibility.Collapsed;
        SysView.Visibility = tag == "system" ? Visibility.Visible : Visibility.Collapsed;
        EventsView.Visibility = tag == "events" ? Visibility.Visible : Visibility.Collapsed;
        ShellView.Visibility = tag == "shell" ? Visibility.Visible : Visibility.Collapsed;
        WindowView.Visibility = tag == "window" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEntryClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RemoteEntry entry && Vm.OpenEntryCommand.CanExecute(entry))
            Vm.OpenEntryCommand.Execute(entry);
    }

    private void OnEntryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is RemoteEntry entry)
            Vm.PullEntry(entry);
    }

    private void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BreadcrumbRow b)
            Vm.NavigateCommand.Execute(b.FullPath);
    }

    private void OnRecentClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentConnection r) Vm.LaunchRecentCommand.Execute(r);
    }

    private void OnRemoveRecent(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecentConnection r) Vm.RemoveRecentCommand.Execute(r);
    }

    private void OnShellInputKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && Vm.RunShellCommand.CanExecute(null))
        {
            e.Handled = true;
            Vm.RunShellCommand.Execute(null);
        }
    }

    /// <summary>x:Bind function helper: WinUI has no built-in bool→Visibility converter.</summary>
    public Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private SwitcherWindow? _switcher;
    private readonly EdgeTrigger _edge;
    private readonly EdgeHandleWindow _handle;
    private readonly FloatingButtonWindow _floating;
    private readonly TrayIcon _tray;
    private bool _switcherVisible;
    private bool _exiting;

    /// <summary>Show the reveal affordance the user picked (edge nub or floating circle); the edge-slam
    /// reveal only applies to the nub.</summary>
    private void ShowActiveHandle()
    {
        if (Vm.UseFloatingButton)
        {
            _handle.AppWindow.Hide();
            _floating.AppWindow.Show(false);
            _edge.Enabled = false;
        }
        else
        {
            _floating.AppWindow.Hide();
            _handle.AppWindow.Show(false);
            _edge.Enabled = true;
        }
    }

    private void HideActiveHandle()
    {
        _handle.AppWindow.Hide();
        _floating.AppWindow.Hide();
    }

    private void OnTrayCommand(int id)
    {
        switch (id)
        {
            case TrayIcon.CmdShowSwitcher: ShowSwitcher(); break;
            case TrayIcon.CmdLaunch: Vm.LaunchCommand.Execute(null); break;
            case TrayIcon.CmdDockLeft: Vm.DockRight = false; break;
            case TrayIcon.CmdDockRight: Vm.DockRight = true; break;
            case TrayIcon.CmdAutoCycle: Vm.AutoCycle = !Vm.AutoCycle; break;
            case TrayIcon.CmdConnectionBar: Vm.ShowConnectionBar = !Vm.ShowConnectionBar; break;
            case TrayIcon.CmdFloating: Vm.UseFloatingButton = !Vm.UseFloatingButton; break;
            case TrayIcon.CmdHotkeys: Vm.HotkeysEnabled = !Vm.HotkeysEnabled; break;
            case TrayIcon.CmdDashboard: AppWindow.Show(); Activate(); break;
            case TrayIcon.CmdExit:
                _exiting = true;
                _tray.Dispose();
                Application.Current.Exit();
                break;
        }
    }

    /// <summary>The switcher is created once and shown/hidden (not closed), so the edge trigger and the
    /// Hide button drive the same instance. It shares this window's view model, so it lists the same
    /// live connections and clicking one runs the same activation path.</summary>
    private void EnsureSwitcher()
    {
        if (_switcher is not null) return;
        _switcher = new SwitcherWindow(Vm);
        _switcher.HideRequested += HideSwitcher;
        _switcher.Closed += (_, _) => { _switcher = null; _switcherVisible = false; };
        _switcher.DockTo(Vm.DockRight);
    }

    /// <summary>Move the nub, panel and reveal edge to the chosen side.</summary>
    private void ApplyDock()
    {
        _edge.DockRight = Vm.DockRight;
        _handle.DockTo(Vm.DockRight);
        _switcher?.DockTo(Vm.DockRight);
    }

    private void ShowSwitcher(bool activate = true)
    {
        if (_switcherVisible) return;
        EnsureSwitcher();
        HideActiveHandle();                // hide the nub/circle while the full sidebar is up
        _switcher!.AppWindow.Show(activate);
        if (activate) _switcher.Activate();
        _switcherVisible = true;
    }

    private void HideSwitcher()
    {
        _switcherVisible = false;
        _switcher?.AppWindow.Hide();
        ShowActiveHandle();                // bring the nub/circle back
    }

    private void OnToggleSwitcher(object sender, RoutedEventArgs e)
    {
        if (_switcherVisible) HideSwitcher(); else ShowSwitcher();
    }
}
