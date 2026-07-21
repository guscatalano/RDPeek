using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Rdpeek.Companion.WinUI;

public sealed partial class MainWindow : Window
{
    public MainViewModel Vm { get; }

    public MainWindow()
    {
        Vm = new MainViewModel(DispatcherQueue);
        InitializeComponent();
        Title = "RDPeek Companion";
    }

    private void OnNavChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "dash";
        DashView.Visibility = tag == "dash" ? Visibility.Visible : Visibility.Collapsed;
        NetView.Visibility = tag == "net" ? Visibility.Visible : Visibility.Collapsed;
        SessView.Visibility = tag == "sess" ? Visibility.Visible : Visibility.Collapsed;
        SvcView.Visibility = tag == "svc" ? Visibility.Visible : Visibility.Collapsed;
        ChanView.Visibility = tag == "chan" ? Visibility.Visible : Visibility.Collapsed;
    }
}
