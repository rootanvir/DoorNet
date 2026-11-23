using DoorNet.Core.Scanning;

namespace DoorNet.Core.Models;

/// <summary>One host discovered on the local network.</summary>
public sealed record LanDevice(
    string Address,
    string Mac,
    string HostName,
    string Vendor,
    DeviceKind Kind,
    string System,
    IReadOnlyList<int> OpenPorts,
    bool IsActive,
    long LatencyMs)
{
    public string Name => string.IsNullOrWhiteSpace(HostName) || HostName == Address ? "—" : HostName;

    public string VendorText => string.IsNullOrWhiteSpace(Vendor) ? "Unknown vendor" : Vendor;

    public string PortsText => DeviceClassifier.PortsText(OpenPorts);

    public string LatencyText => !IsActive ? "—" : LatencyMs <= 1 ? "<1 ms" : $"{LatencyMs} ms";

    public string KindText => Kind switch
    {
        DeviceKind.ThisDevice => "This device",
        DeviceKind.Router => "Router",
        DeviceKind.Computer => "Computer",
        DeviceKind.Phone => "Phone",
        DeviceKind.Tablet => "Tablet",
        DeviceKind.Printer => "Printer",
        DeviceKind.Tv => "TV",
        DeviceKind.Speaker => "Speaker",
        DeviceKind.Camera => "Camera",
        DeviceKind.Console => "Console",
        DeviceKind.InternetOfThings => "IoT",
        _ => "Unknown"
    };
}
