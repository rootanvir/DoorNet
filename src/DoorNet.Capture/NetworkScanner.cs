using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using DoorNet.Core.Models;
using DoorNet.Core.Scanning;

namespace DoorNet.Capture;

/// <summary>Progress for a running LAN scan.</summary>
public sealed record ScanProgress(string Message, int Completed, int Total);

/// <summary>The result of one scan of the local subnet.</summary>
public sealed record ScanResult(string Subnet, IReadOnlyList<LanDevice> Devices, TimeSpan Duration);

/// <summary>
/// Discovers hosts on the subnet of the active adapter: ICMP for liveness and latency, a
/// fire-and-forget UDP poke plus the ARP cache for hardware addresses (works even when a host
/// ignores ping), reverse DNS for names, a small curated port probe for a system guess, and an
/// OUI lookup for the vendor.
/// </summary>
public sealed class NetworkScanner
{
    private const int MaxHosts = 254;
    private const int ProbeParallelism = 64;
    private const int PortParallelism = 96;
    private const int NameParallelism = 24;
    private const int PingTimeoutMs = 900;
    private const int PortTimeoutMs = 160;
    private const int ArpSettleMs = 1200;

    public async Task<ScanResult> ScanAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var network = ActiveNetwork.Find()
            ?? throw new InvalidOperationException("No active IPv4 network was found.");

        var subnet = LanSubnet.Cidr(network.Address, network.Mask);
        var hosts = LanSubnet.Hosts(network.Address, network.Mask, MaxHosts);
        var mask = LanSubnet.ToUInt32(network.Mask);
        LanSubnet.TryNetworkOf(network.Address, network.Mask, out var networkAddress);
        var networkValue = LanSubnet.ToUInt32(networkAddress);

        progress?.Report(new ScanProgress($"Scanning {subnet}…", 0, hosts.Count));

        // 1. Liveness and latency.
        var probes = new ConcurrentDictionary<IPAddress, Probe>();
        var completed = 0;

        await Parallel.ForEachAsync(
            hosts,
            new ParallelOptions { MaxDegreeOfParallelism = ProbeParallelism, CancellationToken = cancellationToken },
            async (address, token) =>
            {
                var probe = await PingHostAsync(address, token).ConfigureAwait(false);
                probes[address] = probe;

                var done = Interlocked.Increment(ref completed);
                if (done % 16 == 0 || done == hosts.Count)
                    progress?.Report(new ScanProgress($"Probing {subnet} · {done}/{hosts.Count}", done, hosts.Count));
            }).ConfigureAwait(false);

        // 2. Nudge the OS into resolving ARP for every address, then read the neighbour cache.
        //    This finds hosts that ignore ICMP, without waiting on a synchronous SendARP per
        //    address (which can take seconds each on a mostly-empty subnet).
        PokeArp(hosts);
        await Task.Delay(ArpSettleMs, cancellationToken).ConfigureAwait(false);

        foreach (var (address, mac) in ReadArpCache(networkValue, mask))
        {
            if (probes.TryGetValue(address, out var known))
            {
                if (known.Mac.Length == 0)
                    probes[address] = known with { Mac = mac };
            }
            else
            {
                // A neighbour the machine has talked to recently but that is quiet right now.
                probes[address] = new Probe(address, Alive: false, LatencyMs: 0, mac);
            }
        }

        // 3. Anything that answered but still has no address: a targeted ARP retry.
        var missingMac = probes.Values.Where(p => p.Alive && p.Mac.Length == 0).ToList();
        if (missingMac.Count > 0)
        {
            await Parallel.ForEachAsync(
                missingMac,
                new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
                async (probe, token) =>
                {
                    var mac = await Task.Run(() => TryReadMac(probe.Address), token).ConfigureAwait(false);
                    if (mac.Length > 0)
                        probes[probe.Address] = probe with { Mac = mac };
                }).ConfigureAwait(false);
        }

        var candidates = probes.Values
            .Where(p => p.Alive || p.Mac.Length > 0)
            .OrderBy(p => LanSubnet.ToUInt32(p.Address))
            .ToList();

        progress?.Report(new ScanProgress(
            $"Reading {candidates.Count} hosts…", hosts.Count, hosts.Count));

        // 4. Names and ports.
        var names = new ConcurrentDictionary<IPAddress, string>();
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = NameParallelism, CancellationToken = cancellationToken },
            async (probe, token) =>
            {
                var name = await ResolveNameAsync(probe.Address, token).ConfigureAwait(false);
                if (name.Length > 0)
                    names[probe.Address] = name;
            }).ConfigureAwait(false);

        // Most home devices publish no PTR record; NetBIOS still knows many Windows machines.
        var unnamed = candidates.Where(p => !names.ContainsKey(p.Address)).ToList();
        await Parallel.ForEachAsync(
            unnamed,
            new ParallelOptions { MaxDegreeOfParallelism = NameParallelism, CancellationToken = cancellationToken },
            async (probe, token) =>
            {
                var name = await TryNetBiosNameAsync(probe.Address, token).ConfigureAwait(false);
                if (name.Length > 0)
                    names[probe.Address] = name;
            }).ConfigureAwait(false);

        // Phones, printers and IoT gear often only answer multicast DNS.
        var stillUnnamed = candidates.Where(p => !names.ContainsKey(p.Address)).ToList();
        await Parallel.ForEachAsync(
            stillUnnamed,
            new ParallelOptions { MaxDegreeOfParallelism = NameParallelism, CancellationToken = cancellationToken },
            async (probe, token) =>
            {
                var name = await TryMdnsNameAsync(probe.Address, token).ConfigureAwait(false);
                if (name.Length > 0)
                    names[probe.Address] = name;
            }).ConfigureAwait(false);

        var ports = await ScanPortsAsync(candidates.Select(p => p.Address).ToList(), progress, cancellationToken)
            .ConfigureAwait(false);

        var devices = new List<LanDevice>(candidates.Count + 1);

        foreach (var probe in candidates)
        {
            var name = names.TryGetValue(probe.Address, out var resolved) ? resolved : string.Empty;
            var openPorts = ports.TryGetValue(probe.Address, out var found)
                ? found
                : (IReadOnlyList<int>)Array.Empty<int>();

            var vendor = OuiVendors.Lookup(probe.Mac);
            var (kind, system) = DeviceClassifier.Classify(
                vendor,
                name,
                openPorts,
                isGateway: network.Gateway is not null && probe.Address.Equals(network.Gateway),
                isThisDevice: false);

            devices.Add(new LanDevice(
                probe.Address.ToString(),
                probe.Mac,
                name,
                vendor ?? string.Empty,
                kind,
                system,
                openPorts,
                probe.Alive,
                probe.LatencyMs));
        }

        // The machine running DoorNet is always part of the picture.
        var localMac = network.Adapter.GetPhysicalAddress();
        var localMacText = MacAddress.Format(localMac.GetAddressBytes());
        devices.Insert(0, new LanDevice(
            network.Address.ToString(),
            localMacText,
            SafeMachineName(),
            OuiVendors.Lookup(localMacText) ?? string.Empty,
            DeviceKind.ThisDevice,
            OperatingSystem.IsWindows() ? "Windows" : "This machine",
            Array.Empty<int>(),
            true,
            0));

        devices = devices
            .OrderBy(d => LanSubnet.ToUInt32(IPAddress.Parse(d.Address)))
            .ToList();

        stopwatch.Stop();

        progress?.Report(new ScanProgress(
            $"{devices.Count} devices · {devices.Count(d => d.IsActive)} active", hosts.Count, hosts.Count));

        return new ScanResult(subnet, devices, stopwatch.Elapsed);
    }

    private static async Task<Probe> PingHostAsync(IPAddress address, CancellationToken token)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, PingTimeoutMs).WaitAsync(token).ConfigureAwait(false);
            return new Probe(address, reply.Status == IPStatus.Success, reply.RoundtripTime, string.Empty);
        }
        catch (Exception ex) when (ex is PingException or SocketException or TimeoutException)
        {
            return new Probe(address, false, 0, string.Empty);
        }
    }

    /// <summary>
    /// Sends one datagram per host so the stack issues an ARP request for each. The packets are
    /// expected to be dropped; the side effect we want is the neighbour cache entry.
    /// </summary>
    private static void PokeArp(IReadOnlyList<IPAddress> hosts)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var payload = new byte[] { 0 };

            foreach (var host in hosts)
            {
                try
                {
                    udp.Send(payload, payload.Length, new IPEndPoint(host, 9));
                }
                catch (SocketException)
                {
                    // Queue full or address unreachable; the remaining pokes still matter.
                }
            }
        }
        catch (SocketException)
        {
        }
    }

    private static Dictionary<IPAddress, string> ReadArpCache(uint network, uint mask)
    {
        var result = new Dictionary<IPAddress, string>();
        var size = 0;

        try
        {
            if (GetIpNetTable(IntPtr.Zero, ref size, false) != 0 || size <= 0)
                return result;

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetIpNetTable(buffer, ref size, false) != 0)
                    return result;

                var rows = Marshal.ReadInt32(buffer);
                for (var i = 0; i < rows; i++)
                {
                    var row = IntPtr.Add(buffer, 4 + (i * 24));
                    var length = Marshal.ReadInt32(row, 4);
                    if (length != 6)
                        continue;

                    var macBytes = new byte[6];
                    Marshal.Copy(IntPtr.Add(row, 8), macBytes, 0, 6);

                    var addressBits = Marshal.ReadInt32(row, 16);
                    var address = new IPAddress(BitConverter.GetBytes(addressBits));
                    if ((LanSubnet.ToUInt32(address) & mask) != network)
                        continue;

                    if (!MacAddress.IsUnicast(macBytes))
                        continue;

                    result[address] = MacAddress.Format(macBytes);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException)
        {
        }

        return result;
    }

    private static string TryReadMac(IPAddress address)
    {
        var mac = new byte[6];
        var length = (uint)mac.Length;

        try
        {
            // SendARP expects the IN_ADDR as it sits in memory (network order bytes).
            var destination = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
            var result = SendARP(destination, 0, mac, ref length);
            if (result == 0 && length >= 6 && MacAddress.IsUnicast(mac))
                return MacAddress.Format(mac);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return string.Empty;
    }

    private static async Task<string> ResolveNameAsync(IPAddress address, CancellationToken token)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString())
                .WaitAsync(TimeSpan.FromMilliseconds(800), token)
                .ConfigureAwait(false);

            return entry.HostName ?? string.Empty;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or TimeoutException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Asks a host for its NetBIOS name table (NBSTAT, UDP 137). Many Windows machines answer
    /// even when they publish no reverse DNS record. Linux, macOS and phones simply time out.
    /// </summary>
    private static async Task<string> TryNetBiosNameAsync(IPAddress address, CancellationToken token)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var query = BuildNetBiosQuery();

            await udp.SendAsync(query.AsMemory(), new IPEndPoint(address, 137), token).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(350);

            var response = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            return ParseNetBiosName(response.Buffer);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return string.Empty;
        }
    }

    private static byte[] BuildNetBiosQuery()
    {
        var query = new byte[50];
        query[0] = 0x82;
        query[1] = 0x28;
        query[5] = 0x01;   // one question
        query[12] = 0x20;  // 32 encoded nibbles
        query[13] = (byte)'C'; // '*' = 0x2A -> 'C','K'
        query[14] = (byte)'K';

        for (var i = 15; i < 45; i++)
            query[i] = (byte)'A'; // the 15 zero bytes of the wildcard name

        query[45] = 0x00;
        query[46] = 0x00;
        query[47] = 0x21; // NBSTAT
        query[48] = 0x00;
        query[49] = 0x01; // class IN
        return query;
    }

    private static string ParseNetBiosName(byte[] buffer)
    {
        if (buffer.Length < 12)
            return string.Empty;

        var questions = (buffer[4] << 8) | buffer[5];
        var answers = (buffer[6] << 8) | buffer[7];
        if (answers == 0)
            return string.Empty;

        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            offset = SkipDnsName(buffer, offset) + 4;
            if (offset >= buffer.Length)
                return string.Empty;
        }

        offset = SkipDnsName(buffer, offset);
        offset += 8; // type, class, TTL

        if (offset + 2 > buffer.Length)
            return string.Empty;

        var dataLength = (buffer[offset] << 8) | buffer[offset + 1];
        offset += 2;
        if (dataLength == 0 || offset >= buffer.Length)
            return string.Empty;

        var count = buffer[offset];
        var fallback = string.Empty;

        for (var i = 0; i < count; i++)
        {
            var entry = offset + 1 + (i * 18);
            if (entry + 18 > buffer.Length)
                break;

            var flags = (buffer[entry + 16] << 8) | buffer[entry + 17];
            if ((flags & 0x8000) != 0)
                continue; // group name

            var text = Encoding.ASCII.GetString(buffer, entry, 15).Trim();
            if (text.Length == 0)
                continue;

            var suffix = buffer[entry + 15];
            if (suffix is 0x00 or 0x20)
                return text; // workstation or server

            if (fallback.Length == 0)
                fallback = text;
        }

        return fallback;
    }

    private static int SkipDnsName(byte[] buffer, int offset)
    {
        while (offset < buffer.Length)
        {
            var length = buffer[offset];
            if (length == 0)
                return offset + 1;

            if ((length & 0xC0) == 0xC0)
                return offset + 2; // compression pointer

            offset += length + 1;
        }

        return offset;
    }

    /// <summary>
    /// Multicast DNS reverse lookup: asks 224.0.0.251 for the PTR of the address, with the
    /// unicast-response bit set so phones and printers that publish a .local name answer us
    /// directly. Silent hosts simply time out.
    /// </summary>
    private static async Task<string> TryMdnsNameAsync(IPAddress address, CancellationToken token)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return string.Empty;

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var query = BuildMdnsReverseQuery(address);

            await udp.SendAsync(query.AsMemory(), new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353), token)
                .ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(500);

            var response = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            return ParseMdnsPtr(response.Buffer);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or FormatException)
        {
            return string.Empty;
        }
    }

    private static byte[] BuildMdnsReverseQuery(IPAddress address)
    {
        var packet = new List<byte> { 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        foreach (var label in address.ToString().Split('.'))
        {
            packet.Add((byte)label.Length);
            packet.AddRange(Encoding.ASCII.GetBytes(label));
        }

        foreach (var label in new[] { "in-addr", "arpa" })
        {
            packet.Add((byte)label.Length);
            packet.AddRange(Encoding.ASCII.GetBytes(label));
        }

        packet.Add(0x00);       // end of name
        packet.Add(0x00);
        packet.Add(0x0C);       // PTR
        packet.Add(0x80);       // unicast response requested
        packet.Add(0x01);       // IN
        return packet.ToArray();
    }

    private static string ParseMdnsPtr(byte[] buffer)
    {
        if (buffer.Length < 12)
            return string.Empty;

        var questions = (buffer[4] << 8) | buffer[5];
        var answers = (buffer[6] << 8) | buffer[7];

        var offset = 12;

        for (var i = 0; i < questions && offset < buffer.Length; i++)
        {
            ReadDnsName(buffer, ref offset);
            offset += 4;
        }

        for (var i = 0; i < answers && offset < buffer.Length; i++)
        {
            ReadDnsName(buffer, ref offset);

            if (offset + 10 > buffer.Length)
                break;

            var type = (buffer[offset] << 8) | buffer[offset + 1];
            var length = (buffer[offset + 8] << 8) | buffer[offset + 9];
            offset += 10;

            if (offset + length > buffer.Length)
                break;

            if (type == 12)
            {
                var nameOffset = offset;
                var name = ReadDnsName(buffer, ref nameOffset);
                if (name.Length > 0)
                    return name.TrimEnd('.');
            }

            offset += length;
        }

        return string.Empty;
    }

    private static string ReadDnsName(byte[] buffer, ref int offset)
    {
        var labels = new List<string>();
        var jumped = false;
        var safety = 0;

        while (offset >= 0 && offset < buffer.Length && safety++ < 64)
        {
            var length = buffer[offset];

            if (length == 0)
            {
                if (!jumped)
                    offset += 1;

                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (offset + 1 >= buffer.Length)
                    break;

                var pointer = ((length & 0x3F) << 8) | buffer[offset + 1];
                if (!jumped)
                    offset += 2;

                jumped = true;
                offset = pointer;
                continue;
            }

            if (offset + 1 + length > buffer.Length)
                break;

            labels.Add(Encoding.ASCII.GetString(buffer, offset + 1, length));
            offset += 1 + length;
        }

        return string.Join(".", labels);
    }

    private static async Task<Dictionary<IPAddress, IReadOnlyList<int>>> ScanPortsAsync(
        IReadOnlyList<IPAddress> hosts,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var open = new ConcurrentDictionary<IPAddress, ConcurrentBag<int>>();
        var probes = hosts
            .SelectMany(host => DeviceClassifier.InterestingPorts.Select(port => (Host: host, Port: port)))
            .ToList();

        var completed = 0;

        await Parallel.ForEachAsync(
            probes,
            new ParallelOptions { MaxDegreeOfParallelism = PortParallelism, CancellationToken = cancellationToken },
            async (probe, token) =>
            {
                if (await IsPortOpenAsync(probe.Host, probe.Port, token).ConfigureAwait(false))
                {
                    var bag = open.GetOrAdd(probe.Host, _ => []);
                    bag.Add(probe.Port);
                }

                var done = Interlocked.Increment(ref completed);
                if (done % 128 == 0)
                    progress?.Report(new ScanProgress(
                        $"Probing ports · {done}/{probes.Count}", done, probes.Count));
            }).ConfigureAwait(false);

        return open.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<int>)pair.Value.OrderBy(port => port).ToArray());
    }

    private static async Task<bool> IsPortOpenAsync(IPAddress host, int port, CancellationToken token)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(PortTimeoutMs);

        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static string SafeMachineName()
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

    private sealed record Probe(IPAddress Address, bool Alive, long LatencyMs, string Mac);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destination, uint source, [Out] byte[] macAddress, ref uint length);

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetTable(IntPtr table, ref int size, bool order);
}
