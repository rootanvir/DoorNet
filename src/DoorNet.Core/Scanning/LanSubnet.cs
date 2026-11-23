using System.Net;
using System.Net.Sockets;

namespace DoorNet.Core.Scanning;

/// <summary>Subnet maths for a local IPv4 network.</summary>
public static class LanSubnet
{
    /// <summary>Upper bound on hosts per scan; a /16 is narrowed to the local /24.</summary>
    public const int DefaultMaxHosts = 254;

    public static bool TryNetworkOf(IPAddress address, IPAddress mask, out IPAddress network)
    {
        network = IPAddress.None;

        if (address.AddressFamily != AddressFamily.InterNetwork
            || mask.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var ip = ToUInt32(address);
        var m = ToUInt32(mask);
        network = FromUInt32(ip & m);
        return true;
    }

    /// <summary>
    /// Every usable host address in the subnet, capped at <paramref name="maxHosts"/>. A
    /// subnet larger than the cap is narrowed to the /24 that contains <paramref name="address"/>,
    /// so a /16 never turns into a 65k-host sweep.
    /// </summary>
    public static IReadOnlyList<IPAddress> Hosts(IPAddress address, IPAddress mask, int maxHosts = DefaultMaxHosts)
    {
        if (!TryNetworkOf(address, mask, out var network))
            return Array.Empty<IPAddress>();

        var ip = ToUInt32(address);
        var m = ToUInt32(mask);

        if (PrefixLength(m) < 24)
            m = 0xFFFFFF00; // narrow to a /24 around the machine

        var networkValue = ip & m;
        var broadcast = networkValue | ~m;
        var total = (long)broadcast - networkValue - 1;

        if (total <= 0)
            return Array.Empty<IPAddress>();

        var limit = (int)Math.Min(total, maxHosts);
        var hosts = new List<IPAddress>(limit);

        // Start one past the network address; skip the local machine so callers can add it.
        for (long offset = 1; offset <= limit; offset++)
        {
            var candidate = networkValue + (uint)offset;
            if (candidate == ip)
                continue;

            hosts.Add(FromUInt32(candidate));
        }

        return hosts;
    }

    /// <summary><c>192.168.0.0/24</c> style label for the subnet.</summary>
    public static string Cidr(IPAddress address, IPAddress mask)
    {
        if (!TryNetworkOf(address, mask, out var network))
            return "unknown";

        var prefix = PrefixLength(ToUInt32(mask));
        return $"{network}/{prefix}";
    }

    public static int PrefixLength(uint mask)
    {
        var bits = 0;
        for (var i = 31; i >= 0; i--)
        {
            if ((mask & (1u << i)) == 0)
                break;

            bits++;
        }

        return bits;
    }

    public static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    public static IPAddress FromUInt32(uint value) =>
        new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
}
