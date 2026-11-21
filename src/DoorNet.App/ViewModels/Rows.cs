using System.Windows.Media;

namespace DoorNet.App.ViewModels;

/// <summary>A flat row for the connection grid.</summary>
public sealed record ConnectionRow(
    string Key,
    string ProcessName,
    string Protocol,
    string Local,
    string Remote,
    string State,
    Brush StateBrush,
    ImageSource? Icon,
    string? ExecutablePath,
    bool IsBlocked,
    string Tooltip)
{
    public static readonly Brush LiveBrush = Frozen("#35E08E");
    public static readonly Brush ListenBrush = Frozen("#5B8FB9");
    public static readonly Brush IdleBrush = Frozen("#5A6C7E");
    public static readonly Brush ClosingBrush = Frozen("#F5B95C");

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>A flat row for the per-application list.</summary>
public sealed record AppRow(
    int ProcessId,
    string Name,
    string Initial,
    Brush Accent,
    ImageSource? Icon,
    string DetailText,
    string DownloadText,
    string UploadText,
    string DownloadTotalText,
    string UploadTotalText,
    double DownloadScale,
    double UploadScale,
    bool DownloadIdle,
    bool UploadIdle,
    bool ShowMeters,
    string? ExecutablePath,
    bool IsBlocked,
    bool InBlocked,
    bool OutBlocked,
    string Tooltip)
{
    /// <summary>False for protected processes the shell cannot give us an image for.</summary>
    public bool HasIcon => Icon is not null;

    public string InStateText => InBlocked ? "Blocked" : "Allowed";

    public string OutStateText => OutBlocked ? "Blocked" : "Allowed";
}

/// <summary>Deterministic accent colours so an app keeps the same badge across runs.</summary>
public static class AccentPalette
{
    private static readonly Brush[] Colors =
    [
        Frozen("#3DDC97"), Frozen("#4C9BFF"), Frozen("#A78BFA"), Frozen("#F5B95C"),
        Frozen("#F27983"), Frozen("#2DD4BF"), Frozen("#E879C7"), Frozen("#9CCC65")
    ];

    public static Brush For(string name)
    {
        var hash = 17;
        foreach (var c in name)
            hash = unchecked((hash * 31) + c);

        return Colors[Math.Abs(hash) % Colors.Length];
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
