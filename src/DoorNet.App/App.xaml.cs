using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DoorNet.App.Platform;

namespace DoorNet.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var settings = SettingsStore.Load();

        ThemeManager.Apply(
            string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase)
                ? AppTheme.Light
                : AppTheme.Dark);

        // Per-app byte usage and firewall rules both need administrator rights. Elevating
        // at launch means the dashboard is complete from the first tick instead of showing
        // sockets until the user happens to block something. Cancelling UAC simply keeps
        // the limited, unprivileged view; --no-elevate skips the prompt entirely.
        var skipElevation = e.Args.Any(arg =>
            string.Equals(arg, "--no-elevate", StringComparison.OrdinalIgnoreCase));

        if (!skipElevation && settings.AutoElevate && !Environment.IsPrivilegedProcess && TryRelaunchElevated())
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // No StartupUri: the window is created here so a relaunch can exit before it exists.
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private static bool TryRelaunchElevated()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas"
            });

            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // UAC was dismissed (1223) or there is no interactive shell: run unprivileged.
            return false;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep a monitoring session alive through a bad tick; surface the reason instead.
        MessageBox.Show(
            $"DoorNet hit an unexpected error and will keep running.\n\n{e.Exception.Message}",
            "DoorNet",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }
}
