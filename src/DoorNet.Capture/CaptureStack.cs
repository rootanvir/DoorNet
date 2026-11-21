namespace DoorNet.Capture;

/// <summary>
/// Default capture stack. Keeping construction in one place means an out-of-process
/// collector can replace the ETW source later without the UI knowing.
/// </summary>
public static class CaptureStack
{
    public static DoorNet.Core.Abstractions.IThroughputSource CreateThroughputSource() =>
        new NetworkInterfaceThroughputSource();

    public static DoorNet.Core.Abstractions.IConnectionSource CreateConnectionSource() =>
        new ConnectionTableSource();

    public static DoorNet.Core.Abstractions.IPerAppUsageSource CreatePerAppUsageSource() =>
        new EtwPerAppUsageSource();

    public static DoorNet.Core.Firewall.IFirewallController CreateFirewallController() =>
        new WindowsFirewallController();
}
