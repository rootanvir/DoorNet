using System.Windows;
using System.Windows.Media;

namespace DoorNet.App.Platform;

/// <summary>
/// Applies a palette to the live UI. Rather than swapping the merged dictionary (which does
/// not reliably repaint because lookup precedence keeps the old palette winning), the
/// existing brush instances are recoloured in place — every DynamicResource/StaticResource
/// reference points at those brushes, so the whole window updates immediately.
/// </summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static void Apply(AppTheme theme)
    {
        var app = Application.Current;
        if (app is null)
            return;

        var palette = new ResourceDictionary
        {
            Source = new Uri(
                theme == AppTheme.Light ? "Styles/Palette.Light.xaml" : "Styles/Palette.Dark.xaml",
                UriKind.Relative)
        };

        foreach (var key in palette.Keys)
        {
            if (palette[key] is not SolidColorBrush replacement)
                continue;

            if (app.TryFindResource(key) is SolidColorBrush existing && !existing.IsFrozen)
            {
                if (existing.Color != replacement.Color)
                    existing.Color = replacement.Color;
            }
            else
            {
                // Frozen (or missing) brush: replace the resource; DynamicResource
                // consumers re-resolve and paint with the new instance.
                app.Resources[key] = new SolidColorBrush(replacement.Color);
            }
        }

        Current = theme;
    }
}
