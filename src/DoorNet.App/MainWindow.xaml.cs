using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DoorNet.App.ViewModels;

namespace DoorNet.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ViewModel = new MainViewModel();
        DataContext = ViewModel;
        Closed += (_, _) => ViewModel.Dispose();
    }

    public MainViewModel ViewModel { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableDarkTitleBar();
    }

    /// <summary>Matches the native title bar to the dark canvas where the OS supports it.</summary>
    private void TryEnableDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var enabled = 1;

            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+), 19 on older builds.
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows: keep the default title bar.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
