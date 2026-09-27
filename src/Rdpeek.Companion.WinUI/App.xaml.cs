using System;
using System.Linq;
using Microsoft.UI.Xaml;

namespace Rdpeek.Companion.WinUI;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var win = new MainWindow();
        _window = win;
        _window.Activate();   // activate so the dispatcher/tray are fully live…
        // …then, when auto-started on logon (--tray), drop straight to the tray instead of showing.
        if (Environment.GetCommandLineArgs().Any(a => a.Equals(StartupManager.TrayArg, StringComparison.OrdinalIgnoreCase)))
            win.HideToTray();
    }
}
