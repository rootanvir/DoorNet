namespace DoorNet.Core.Scanning;

/// <summary>Coarse device category, used for the row icon and label.</summary>
public enum DeviceKind
{
    Unknown,
    ThisDevice,
    Router,
    Computer,
    Phone,
    Tablet,
    Printer,
    Tv,
    Speaker,
    Camera,
    Console,
    InternetOfThings
}

/// <summary>
/// Turns the noisy facts a LAN scan can collect - vendor, host name, open ports, whether the
/// address is the gateway - into a device type and a best-effort system label.
/// </summary>
public static class DeviceClassifier
{
    /// <summary>Ports probed on responsive hosts; also the order shown in the UI.</summary>
    public static readonly IReadOnlyList<int> InterestingPorts =
    [
        22, 53, 80, 135, 139, 443, 445, 515, 631, 3389, 5357, 8008, 8009, 8060, 8443, 9100, 62078
    ];

    public static (DeviceKind Kind, string System) Classify(
        string? vendor,
        string? hostName,
        IReadOnlyCollection<int> openPorts,
        bool isGateway,
        bool isThisDevice)
    {
        if (isThisDevice)
            return (DeviceKind.ThisDevice, OperatingSystem.IsWindows() ? "Windows" : "This machine");

        var vendorText = vendor ?? string.Empty;
        var host = (hostName ?? string.Empty).ToLowerInvariant();

        if (IsPrinterPorts(openPorts) || host.Contains("printer", StringComparison.Ordinal))
            return (DeviceKind.Printer, "Printer");

        if (LooksLikeTv(host, vendorText, openPorts))
            return (DeviceKind.Tv, "Smart TV");

        if (Has(openPorts, 62078))
            return host.Contains("ipad", StringComparison.Ordinal)
                ? (DeviceKind.Tablet, "iPadOS")
                : (DeviceKind.Phone, "iOS");

        if (isGateway || Has(openPorts, 53) || IsRouterVendor(vendorText))
            return (DeviceKind.Router, "Router firmware");

        if (HasAny(openPorts, 135, 139, 445, 3389, 5357))
            return (DeviceKind.Computer, "Windows");

        if (Has(openPorts, 22))
            return (DeviceKind.Computer, "Linux / Unix");

        if (IsSpeakerVendor(vendorText, host))
            return (DeviceKind.Speaker, "Audio");

        if (IsCameraVendor(vendorText, host))
            return (DeviceKind.Camera, "Camera");

        if (host.Contains("switch", StringComparison.Ordinal) || host.Contains("xbox", StringComparison.Ordinal)
            || host.Contains("playstation", StringComparison.Ordinal))
            return (DeviceKind.Console, "Game console");

        if (IsPhoneVendor(vendorText, host))
            return host.Contains("tab", StringComparison.Ordinal)
                ? (DeviceKind.Tablet, "Android")
                : (DeviceKind.Phone, "Android");

        if (IsIotVendor(vendorText, host))
            return (DeviceKind.InternetOfThings, "Embedded");

        if (vendorText.Contains("Apple", StringComparison.OrdinalIgnoreCase))
            return (DeviceKind.Computer, "macOS");

        if (IsComputerVendor(vendorText))
            return (DeviceKind.Computer, "PC");

        if (HasAny(openPorts, 80, 443, 8008, 8443))
            return (DeviceKind.InternetOfThings, "Web interface");

        return (DeviceKind.Unknown, "—");
    }

    /// <summary>Compact "22, 80, 443" summary or an em dash.</summary>
    public static string PortsText(IReadOnlyList<int> ports) =>
        ports.Count == 0 ? "—" : string.Join(", ", ports);

    private static bool Has(IReadOnlyCollection<int> ports, int port) => ports.Contains(port);

    private static bool HasAny(IReadOnlyCollection<int> ports, params int[] candidates) =>
        candidates.Any(ports.Contains);

    private static bool IsPrinterPorts(IReadOnlyCollection<int> ports) =>
        HasAny(ports, 9100, 515, 631);

    private static bool LooksLikeTv(string host, string vendor, IReadOnlyCollection<int> ports) =>
        HasAny(ports, 8008, 8009, 8060)
        || vendor.Contains("Roku", StringComparison.OrdinalIgnoreCase)
        || host.Contains("tv", StringComparison.Ordinal)
        || host.Contains("chromecast", StringComparison.Ordinal)
        || host.Contains("shield", StringComparison.Ordinal);

    private static bool IsRouterVendor(string vendor) =>
        ContainsAny(vendor, "TP-Link", "Netgear", "ASUS", "D-Link", "Cisco", "Ubiquiti", "ARRIS", "MikroTik", "Huawei");

    private static bool IsSpeakerVendor(string vendor, string host) =>
        vendor.Contains("Sonos", StringComparison.OrdinalIgnoreCase)
        || host.Contains("speaker", StringComparison.Ordinal)
        || host.Contains("sonos", StringComparison.Ordinal);

    private static bool IsCameraVendor(string vendor, string host) =>
        ContainsAny(vendor, "Hikvision", "Dahua", "Wyze", "Ring")
        || host.Contains("cam", StringComparison.Ordinal);

    private static bool IsPhoneVendor(string vendor, string host) =>
        ContainsAny(vendor, "Samsung", "Xiaomi", "OnePlus", "OPPO", "vivo", "Motorola", "Nokia")
        || host.Contains("phone", StringComparison.Ordinal)
        || host.Contains("android", StringComparison.Ordinal);

    private static bool IsIotVendor(string vendor, string host) =>
        ContainsAny(vendor, "Espressif", "Tuya", "Shelly", "Sonoff", "Google", "Amazon", "Philips")
        || host.Contains("esp", StringComparison.Ordinal)
        || host.Contains("iot", StringComparison.Ordinal);

    private static bool IsComputerVendor(string vendor) =>
        ContainsAny(vendor, "Intel", "Realtek", "Broadcom", "Qualcomm", "AzureWave", "Micro-Star",
            "ASUSTeK", "Dell", "HP", "Lenovo", "Raspberry Pi", "VMware", "VirtualBox", "QEMU", "Parallels");

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
}
