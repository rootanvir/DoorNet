using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DoorNet.Core.Scanning;

namespace DoorNet.Capture;

/// <summary>The adapter and address the machine actually uses to reach the internet.</summary>
internal sealed record ActiveNetwork(
    NetworkInterface Adapter,
    IPAddress Address,
    IPAddress Mask,
    IPAddress? Gateway)
{
    public string Subnet => LanSubnet.Cidr(Address, Mask);

    public bool IsWireless => Adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;

    public string MacAddress => DoorNet.Core.Scanning.MacAddress.Format(Adapter.GetPhysicalAddress().GetAddressBytes());

    public static ActiveNetwork? Find()
    {
        ActiveNetwork? fallback = null;

        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var properties = adapter.GetIPProperties();
                var gateway = properties.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a is not null
                        && a.AddressFamily == AddressFamily.InterNetwork
                        && !a.Equals(IPAddress.Any));

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    if (IPAddress.IsLoopback(unicast.Address) || unicast.IPv4Mask is null)
                        continue;

                    var candidate = new ActiveNetwork(adapter, unicast.Address, unicast.IPv4Mask, gateway);

                    if (gateway is not null)
                        return candidate;

                    fallback ??= candidate;
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
        }

        return fallback;
    }
}
