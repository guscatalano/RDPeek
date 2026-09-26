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

        // Reveal the switcher (focused) when the cursor is shoved into the left edge (mid-screen). It
        // auto-hides on blur, so it must take focus to be dismissable that way. Poll-thread callback
        // marshals onto the UI thread.
        _edge = new EdgeTrigger(() => DispatcherQueue.TryEnqueue(() => ShowSwitcher()));
        Closed += (_, _) => _edge?.Dispose();
    }

    private void OnNavChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "dash";
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
    private bool _switcherVisible;

    /// <summary>The switcher is created once and shown/hidden (not closed), so the edge trigger and the
    /// Hide button drive the same instance. It shares this window's view model, so it lists the same
    /// live connections and clicking one runs the same activation path.</summary>
    private void EnsureSwitcher()
    {
        if (_switcher is not null) return;
        _switcher = new SwitcherWindow(Vm);
        _switcher.HideRequested += HideSwitcher;
        _switcher.Closed += (_, _) => { _switcher = null; _switcherVisible = false; };
    }

    private void ShowSwitcher(bool activate = true)
    {
        if (_switcherVisible) return;
        EnsureSwitcher();
        _switcher!.AppWindow.Show(activate);
        if (activate) _switcher.Activate();
        _switcherVisible = true;
    }

    private void HideSwitcher()
    {
        if (_switcher is null) return;
        _switcher.AppWindow.Hide();
        _switcherVisible = false;
    }

    private void OnToggleSwitcher(object sender, RoutedEventArgs e)
    {
        if (_switcherVisible) HideSwitcher(); else ShowSwitcher();
    }
}
