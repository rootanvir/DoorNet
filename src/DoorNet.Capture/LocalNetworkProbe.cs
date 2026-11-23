using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DoorNet.Capture;

/// <summary>Everything we can say about the machine's own connection.</summary>
public sealed record LocalNetworkInfo(
    string NetworkName,
    string ConnectionType,
    string AdapterName,
    string AdapterDescription,
    string Ssid,
    string Bssid,
    int SignalPercent,
    string WifiDetails,
    string LocalAddress,
    string MacAddress,
    string Subnet,
    string Gateway,
    string LinkSpeed,
    string HostName,
    string OsVersion,
    string DnsServers);

/// <summary>Reads the active adapter plus, when wireless, the connected SSID and signal.</summary>
public static class LocalNetworkProbe
{
    public static LocalNetworkInfo? Read()
    {
        var network = ActiveNetwork.Find();
        if (network is null)
            return null;

        var adapter = network.Adapter;
        var wifi = network.IsWireless ? WifiReading.Read() : WifiReading.None;

        var connection = adapter.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => "Ethernet",
            NetworkInterfaceType.Ppp => "VPN",
            _ => adapter.NetworkInterfaceType.ToString()
        };

        var networkName = !string.IsNullOrWhiteSpace(wifi.Ssid)
            ? wifi.Ssid
            : adapter.Name;

        var dns = string.Empty;
        try
        {
            dns = string.Join(", ", adapter.GetIPProperties().DnsAddresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString()));
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
        }

        return new LocalNetworkInfo(
            networkName,
            connection,
            adapter.Name,
            adapter.Description,
            wifi.Ssid,
            wifi.Bssid,
            wifi.SignalPercent,
            wifi.Details,
            network.Address.ToString(),
            network.MacAddress,
            network.Subnet,
            network.Gateway?.ToString() ?? "—",
            adapter.Speed > 0 ? $"{adapter.Speed / 1_000_000d:0.#} Mbps" : "—",
            SafeHostName(),
            DescribeOs(),
            dns.Length > 0 ? dns : "—");
    }

    private static string DescribeOs()
    {
        if (!OperatingSystem.IsWindows())
            return Environment.OSVersion.VersionString;

        var version = Environment.OSVersion.Version;
        var name = version.Build >= 22000 ? "Windows 11" : "Windows 10";
        return $"{name} {version.Major}.{version.Minor} · build {version.Build}";
    }

    private static string SafeHostName()
    {
        try
        {
            return Dns.GetHostName();
        }
        catch (SocketException)
        {
            return Environment.MachineName;
        }
    }

    private sealed record WifiReading(string Ssid, string Bssid, int SignalPercent, string Details)
    {
        public static readonly WifiReading None = new(string.Empty, string.Empty, -1, string.Empty);

        public static WifiReading Read()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("netsh", "wlan show interfaces")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process is null)
                    return None;

                var text = process.StandardOutput.ReadToEnd();
                process.WaitForExit(2000);

                string ssid = string.Empty;
                string bssid = string.Empty;
                string signal = string.Empty;
                string radio = string.Empty;
                string channel = string.Empty;
                string rates = string.Empty;

                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.Trim();
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                        continue;

                    var key = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim();

                    switch (key)
                    {
                        case "SSID":
                            ssid = value;
                            break;
                        case "BSSID":
                            bssid = value;
                            break;
                        case "Signal":
                            signal = value;
                            break;
                        case "Radio type":
                            radio = value;
                            break;
                        case "Channel":
                            channel = value;
                            break;
                        case "Receive rate (Mbps)":
                        case "Transmit rate (Mbps)":
                            if (rates.Length == 0)
                                rates = value;
                            break;
                    }
                }

                var percent = int.TryParse(signal.Replace("%", string.Empty).Trim(), out var parsed)
                    ? parsed
                    : -1;

                var details = string.Join(" · ", new[]
                {
                    radio,
                    channel.Length > 0 ? $"channel {channel}" : string.Empty,
                    percent >= 0 ? $"{percent}% signal" : string.Empty
                }.Where(part => !string.IsNullOrWhiteSpace(part)));

                return new WifiReading(ssid, bssid, percent, details);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return None;
            }
        }
    }
}
