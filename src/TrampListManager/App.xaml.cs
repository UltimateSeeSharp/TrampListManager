using System.Windows;
using TrampListManager.Services;

namespace TrampListManager;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // Shown first, then handled: a .wbt double-click or tramplist:// link should land
        // in a visible window with its result on screen, not run headless.
        var request = LaunchRequest.Parse(e.Args);
        if (request is not LaunchRequest.Browse)
            await window.HandleLaunchAsync(request);
    }
}
