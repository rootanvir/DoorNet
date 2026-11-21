using System.Net;

namespace DoorNet.Core.Networking;

/// <summary>
/// Endpoint filtering shared by capture sources. Loopback and unspecified addresses never
/// leave the machine, so they must not appear as network usage — adapter counters exclude
/// them too, and counting them in one view but not the other makes the dashboard lie.
/// </summary>
public static class AddressScope
{
    public static bool IsLocalOnly(IPAddress address) =>
        IPAddress.IsLoopback(address)
        || address.Equals(IPAddress.Any)
        || address.Equals(IPAddress.IPv6Any);
}
