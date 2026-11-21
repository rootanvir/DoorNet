using DoorNet.Core.Aggregation;
using DoorNet.Core.Models;

namespace DoorNet.Core.Tests;

public class AppUsageAggregatorTests
{
    private static ConnectionInfo Connection(
        int pid,
        string name,
        string remote,
        int remotePort,
        string state = "Established") =>
        new()
        {
            Protocol = TransportProtocol.Tcp,
            ProcessId = pid,
            ProcessName = name,
            LocalAddress = "10.0.0.5",
            LocalPort = 50000,
            RemoteAddress = remote,
            RemotePort = remotePort,
            State = state
        };

    [Fact]
    public void GroupsConnectionsByProcess()
    {
        var connections = new[]
        {
            Connection(10, "chrome.exe", "1.1.1.1", 443),
            Connection(10, "chrome.exe", "8.8.8.8", 443),
            Connection(20, "svchost.exe", "0.0.0.0", 0, "Listen")
        };

        var apps = AppUsageAggregator.Aggregate(connections);

        Assert.Equal(2, apps.Count);

        var chrome = apps.Single(a => a.ProcessId == 10);
        Assert.Equal(2, chrome.ConnectionCount);
        Assert.Equal(2, chrome.ActiveConnectionCount);
        Assert.Equal(2, chrome.RemoteHostCount);
        Assert.Equal("C", chrome.Initial);

        var svchost = apps.Single(a => a.ProcessId == 20);
        Assert.Equal(1, svchost.ConnectionCount);
        Assert.Equal(0, svchost.ActiveConnectionCount);
    }

    [Fact]
    public void ListeningSockets_AreNotCountedAsActiveOrRemoteHosts()
    {
        var apps = AppUsageAggregator.Aggregate([Connection(7, "app.exe", "0.0.0.0", 0, "Listen")]);

        Assert.Equal(0, apps[0].ActiveConnectionCount);
        Assert.Equal(0, apps[0].RemoteHostCount);
    }

    [Fact]
    public void MergesPerAppTraffic_WhenAvailable()
    {
        var connections = new[] { Connection(10, "chrome.exe", "1.1.1.1", 443) };
        var traffic = new[] { new PerAppTraffic(10, BytesSent: 500, BytesReceived: 1500) };

        var apps = AppUsageAggregator.Aggregate(connections, traffic);

        Assert.True(apps[0].HasByteUsage);
        Assert.Equal(500, apps[0].BytesSent);
        Assert.Equal(1500, apps[0].BytesReceived);
    }

    [Fact]
    public void EmptyInput_ReturnsNoRows()
    {
        Assert.Empty(AppUsageAggregator.Aggregate([]));
    }
}
