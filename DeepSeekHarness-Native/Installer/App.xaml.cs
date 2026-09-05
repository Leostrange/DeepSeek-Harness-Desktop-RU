using System;
using System.Windows;

namespace DeepSeekHarness.Setup;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var silentIdx = Array.IndexOf(e.Args, "--silent");
        if (silentIdx >= 0)
        {
            var target = silentIdx + 1 < e.Args.Length && !e.Args[silentIdx + 1].StartsWith("--")
                ? e.Args[silentIdx + 1]
                : InstallerCore.DefaultTarget;

            try
            {
                await InstallerCore.EnsureNodeAsync(target);
                InstallerCore.Extract(target);
                InstallerCore.CreateShortcuts(target, startMenu: true, desktop: true, autostart: false);
                Shutdown(0);
            }
            catch
            {
                Shutdown(1);
            }
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
