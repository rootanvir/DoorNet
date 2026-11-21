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

        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
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
