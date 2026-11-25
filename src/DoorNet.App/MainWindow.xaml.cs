using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DoorNet.App.Platform;
using DoorNet.App.ViewModels;

namespace DoorNet.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ViewModel = new MainViewModel();
        DataContext = ViewModel;
        ViewModel.ThemeChanged += (_, _) => ApplyTitleBarTheme();
        Closed += (_, _) => ViewModel.Dispose();
    }

    public MainViewModel ViewModel { get; }

    /// <summary>Opens a filter button's menu on left click.</summary>
    private void OpenFilterMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.ContextMenu is not { } menu)
            return;

        menu.PlacementTarget = element;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Opens a pill's Allow/Block menu on left click, sized and placed to match the pill.</summary>
    private void OpenPillMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.ContextMenu is not { } menu)
            return;

        menu.PlacementTarget = element;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;

        // The popup padding reserves 6 px for the item's shadow; offset it so the visible
        // box lines up with the pill and keeps a 4 px gap below it.
        const double shadowPadding = 6;
        menu.HorizontalOffset = -shadowPadding;
        menu.VerticalOffset = 4 - shadowPadding;
        menu.Padding = new Thickness(0);
        menu.Background = Brushes.Transparent;
        menu.BorderThickness = new Thickness(0);
        menu.Width = element.ActualWidth + (shadowPadding * 2);
        menu.MinWidth = menu.Width;
        menu.IsOpen = true;
        e.Handled = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBarTheme();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // DWM ignores the attribute until the frame exists on some builds.
        ApplyTitleBarTheme();
    }

    /// <summary>Matches the native title bar to the active palette.</summary>
    private void ApplyTitleBarTheme()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
                return;

            var dark = ThemeManager.Current == AppTheme.Dark ? 1 : 0;

            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+), 19 on older builds.
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));

            // Force the non-client area to repaint with the new mode.
            SetWindowPos(
                handle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoSize | SwpNoMove | SwpNoZOrder | SwpFrameChanged);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows: keep the default title bar.
        }
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
