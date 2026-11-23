using System.Windows.Media;
using DoorNet.Core.Scanning;

namespace DoorNet.App.ViewModels;

/// <summary>One host in the scanner table.</summary>
public sealed record ScannerRow(
    string Kind,
    string Glyph,
    string Name,
    string Ip,
    string Mac,
    string System,
    string Vendor,
    string Ports,
    string Latency,
    string StateText,
    Brush StateBrush,
    bool IsActive,
    string Tooltip)
{
    public static readonly Brush ActiveBrush = Frozen("#35E08E");
    public static readonly Brush IdleBrush = Frozen("#5A6C7E");

    public static string GlyphFor(DeviceKind kind) => kind switch
    {
        DeviceKind.ThisDevice or DeviceKind.Computer => "\uE839",
        DeviceKind.Router => "\uE968",
        DeviceKind.Phone => "\uE8EA",
        DeviceKind.Tablet => "\uE70A",
        DeviceKind.Printer => "\uE749",
        DeviceKind.Tv => "\uE7F4",
        DeviceKind.Speaker => "\uE8D6",
        DeviceKind.Camera => "\uE722",
        DeviceKind.Console => "\uE7FC",
        DeviceKind.InternetOfThings => "\uE945",
        _ => "\uE897"
    };

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

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

/// <summary>A flat row for the per-application list, aggregated across processes.</summary>
public sealed record AppRow(
    string GroupKey,
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
    bool InBlocked,
    bool OutBlocked,
    bool IsExpanded,
    IReadOnlyList<AppProcessRow> Processes,
    IReadOnlyList<string> Tabs,
    bool TabsExpanded,
    IReadOnlyList<string> VisibleTabs,
    string TabsPageText,
    bool HasTabPages,
    bool HasPreviousTabs,
    bool HasNextTabs,
    string Tooltip)
{
    /// <summary>False for protected processes the shell cannot give us an image for.</summary>
    public bool HasIcon => Icon is not null;

    public string InStateText => InBlocked ? "Blocked" : "Allowed";

    public string OutStateText => OutBlocked ? "Blocked" : "Allowed";

    public bool HasTabs => Tabs.Count > 0;

    /// <summary>Tabs are only visible while the application row itself is expanded.</summary>
    public bool ShowTabsSection => IsExpanded && HasTabs;

    public string TabsHeader => $"Tabs ({Tabs.Count})";
}

/// <summary>One process behind an aggregated application row.</summary>
public sealed record AppProcessRow(
    int ProcessId,
    string Name,
    string PidText,
    string DetailText,
    string DownloadText,
    string UploadText,
    string TotalText,
    ImageSource? Icon);

/// <summary>Connections for one application, collapsible in the Live connections list.</summary>
public sealed record ConnectionGroupRow(
    string GroupKey,
    string Name,
    string Initial,
    Brush Accent,
    ImageSource? Icon,
    string Summary,
    bool IsExpanded,
    IReadOnlyList<ConnectionRow> Connections,
    string Tooltip,
    string ProtocolSummary,
    string LocalSummary,
    string RemoteSummary,
    string StateSummary,
    Brush StateBrush)
{
    public bool HasIcon => Icon is not null;

    public bool HasConnections => Connections.Count > 0;
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
