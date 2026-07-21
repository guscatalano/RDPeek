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
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        bool channels = tag == "chan";
        DashView.Visibility = channels ? Visibility.Collapsed : Visibility.Visible;
        ChanView.Visibility = channels ? Visibility.Visible : Visibility.Collapsed;
    }
}
