using System.Net;
using System.Net.NetworkInformation;
using DoorNet.Core.Abstractions;
using DoorNet.Core.Models;

namespace DoorNet.Capture;

/// <summary>
/// Reads aggregate byte counters across active physical/virtual adapters.
/// <para>
/// Windows exposes a counter per adapter, and virtual adapters (Hyper-V vEthernet, WSL,
/// VMware host-only) mirror traffic that also crosses the physical NIC. Summing them all
/// double-counts. We therefore follow the single adapter that owns a default gateway and
/// has moved the most bytes — the machine's actual internet path.
/// </para>
/// </summary>
public sealed class NetworkInterfaceThroughputSource : IThroughputSource
{
    public string Describe() => ReadCounters().AdapterSummary;

    public AdapterCounters ReadCounters()
    {
        var candidates = new List<Candidate>();

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            if (adapter.OperationalStatus != OperationalStatus.Up)
                continue;

            try
            {
                var stats = adapter.GetIPStatistics();
                candidates.Add(new Candidate(
                    adapter,
                    (ulong)Math.Max(0, stats.BytesReceived),
                    (ulong)Math.Max(0, stats.BytesSent),
                    HasGateway(adapter)));
            }
            catch (Exception ex) when (ex is NetworkInformationException
                                           or InvalidOperationException
                                           or PlatformNotSupportedException)
            {
                // Adapter disappeared mid-enumeration; skip it rather than kill the tick.
            }
        }

        Candidate? pick = candidates
            .Where(c => c.HasGateway)
            .OrderByDescending(c => c.BytesReceived + c.BytesSent)
            .FirstOrDefault();

        // No default gateway (isolated lab, VPN-only without a gateway entry): fall back to
        // the busiest active adapter instead of reporting nothing.
        if (pick is null)
        {
            pick = candidates
                .OrderByDescending(c => c.BytesReceived + c.BytesSent)
                .FirstOrDefault();
        }

        if (pick is not { } chosen)
            return AdapterCounters.Empty;

        return new AdapterCounters(chosen.BytesReceived, chosen.BytesSent, Describe(chosen.Adapter));
    }

    private static bool HasGateway(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().GatewayAddresses.Any(g =>
                g.Address is not null
                && !g.Address.Equals(IPAddress.Any)
                && !g.Address.Equals(IPAddress.IPv6Any));
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static string Describe(NetworkInterface adapter)
    {
        var kind = adapter.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => "Ethernet",
            NetworkInterfaceType.Ppp => "VPN",
            _ => adapter.NetworkInterfaceType.ToString()
        };

        var speed = adapter.Speed > 0 ? $" · {FormatSpeed(adapter.Speed)}" : string.Empty;
        var name = adapter.Name.Length > 28 ? adapter.Name[..28] + "…" : adapter.Name;

        return $"{kind} · {name}{speed}";
    }

    private static string FormatSpeed(long bitsPerSecond) => bitsPerSecond switch
    {
        >= 1_000_000_000 => $"{bitsPerSecond / 1_000_000_000d:0.#} Gbps",
        >= 1_000_000 => $"{bitsPerSecond / 1_000_000d:0} Mbps",
        _ => $"{bitsPerSecond / 1_000d:0} Kbps"
    };

    private readonly record struct Candidate(
        NetworkInterface Adapter,
        ulong BytesReceived,
        ulong BytesSent,
        bool HasGateway);
}
