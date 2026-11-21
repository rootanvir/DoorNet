using DoorNet.Core.Aggregation;
using DoorNet.Core.Models;

namespace DoorNet.Core.Tests;

public class AppUsageAggregatorTrafficTests
{
    [Fact]
    public void TrafficOnlyProcess_GetsARowWhenItCanBeNamed()
    {
        var traffic = new[]
        {
            new PerAppTraffic(99, BytesSent: 100, BytesReceived: 200, ProcessName: "backup.exe", ExecutablePath: @"C:\backup.exe")
        };

        var apps = AppUsageAggregator.Aggregate([], traffic);

        var row = Assert.Single(apps);
        Assert.Equal(99, row.ProcessId);
        Assert.Equal("backup.exe", row.ProcessName);
        Assert.Equal(100, row.BytesSent);
        Assert.Equal(200, row.BytesReceived);
        Assert.Equal(0, row.ConnectionCount);
    }

    [Fact]
    public void TrafficWithoutAName_IsIgnored()
    {
        var traffic = new[] { new PerAppTraffic(5, BytesSent: 10, BytesReceived: 10) };

        Assert.Empty(AppUsageAggregator.Aggregate([], traffic));
    }

    [Fact]
    public void TrafficForAKnownProcess_AddsToThatRow()
    {
        var connections = new[]
        {
            new ConnectionInfo
            {
                Protocol = TransportProtocol.Tcp,
                ProcessId = 10,
                ProcessName = "chrome.exe",
                LocalAddress = "10.0.0.5",
                LocalPort = 50000,
                RemoteAddress = "1.1.1.1",
                RemotePort = 443,
                State = "Established"
            }
        };

        var traffic = new[]
        {
            new PerAppTraffic(10, BytesSent: 640, BytesReceived: 4096, ProcessName: "Google Chrome")
        };

        var apps = AppUsageAggregator.Aggregate(connections, traffic);

        var row = Assert.Single(apps);
        Assert.Equal(1, row.ConnectionCount);
        Assert.Equal(640, row.BytesSent);
        Assert.Equal(4096, row.BytesReceived);
        Assert.True(row.HasByteUsage);
    }
}
