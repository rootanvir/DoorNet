using DoorNet.Core.Models;

namespace DoorNet.Core.Aggregation;

/// <summary>Rolls a connection snapshot up into one row per application.</summary>
public static class AppUsageAggregator
{
    public static IReadOnlyList<AppUsage> Aggregate(
        IEnumerable<ConnectionInfo> connections,
        IReadOnlyList<PerAppTraffic>? traffic = null)
    {
        var byProcess = new Dictionary<int, Accumulator>();

        foreach (var connection in connections)
        {
            if (!byProcess.TryGetValue(connection.ProcessId, out var acc))
            {
                acc = new Accumulator(connection.ProcessName, connection.ExecutablePath);
                byProcess[connection.ProcessId] = acc;
            }

            acc.ConnectionCount++;

            if (!connection.IsListening && connection.RemotePort != 0)
            {
                acc.ActiveCount++;
                acc.RemoteHosts.Add($"{connection.RemoteAddress}:{connection.RemotePort}");
            }
        }

        if (traffic is not null)
        {
            foreach (var sample in traffic)
            {
                if (!byProcess.TryGetValue(sample.ProcessId, out var acc))
                {
                    // A process can finish its transfer and close its sockets before the
                    // next tick; keep its bytes as long as the collector can name it.
                    if (string.IsNullOrWhiteSpace(sample.ProcessName))
                        continue;

                    acc = new Accumulator(sample.ProcessName, sample.ExecutablePath);
                    byProcess[sample.ProcessId] = acc;
                }

                acc.BytesSent += sample.BytesSent;
                acc.BytesReceived += sample.BytesReceived;
            }
        }

        var rows = new List<AppUsage>(byProcess.Count);
        foreach (var (pid, acc) in byProcess)
        {
            rows.Add(new AppUsage
            {
                ProcessId = pid,
                ProcessName = acc.Name,
                ExecutablePath = acc.Path,
                ConnectionCount = acc.ConnectionCount,
                ActiveConnectionCount = acc.ActiveCount,
                RemoteHostCount = acc.RemoteHosts.Count,
                BytesSent = acc.BytesSent,
                BytesReceived = acc.BytesReceived
            });
        }

        return rows;
    }

    private sealed class Accumulator(string name, string? path)
    {
        public string Name { get; } = string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
        public string? Path { get; } = path;
        public int ConnectionCount;
        public int ActiveCount;
        public long BytesSent;
        public long BytesReceived;
        public HashSet<string> RemoteHosts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
