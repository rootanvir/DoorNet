using DoorNet.Core.Capture;
using DoorNet.Core.Models;

namespace DoorNet.Core.Tests;

public class ThroughputSamplerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstSample_EstablishesBaseline_WithoutReportingTraffic()
    {
        var sampler = new ThroughputSampler();

        var reading = sampler.Sample(new AdapterCounters(5_000, 2_000, "Wi-Fi"), T0);

        Assert.Equal(0, reading.DownloadBytesPerSecond);
        Assert.Equal(0, reading.UploadBytesPerSecond);
        Assert.Equal(0UL, reading.SessionBytesReceived);
        Assert.Equal("Wi-Fi", reading.AdapterSummary);
    }

    [Fact]
    public void SecondSample_ComputesSmoothedRateFromDelta()
    {
        var sampler = new ThroughputSampler(smoothing: 0.35);

        sampler.Sample(new AdapterCounters(1_000, 1_000, "Wi-Fi"), T0);
        var reading = sampler.Sample(new AdapterCounters(2_000, 1_500, "Wi-Fi"), T0.AddSeconds(1));

        // 1000 bytes in 1 s = 1000 B/s instant, pulled toward the previous 0 by alpha.
        Assert.Equal(350, reading.DownloadBytesPerSecond, precision: 6);
        Assert.Equal(175, reading.UploadBytesPerSecond, precision: 6);
        Assert.Equal(1_000UL, reading.SessionBytesReceived);
        Assert.Equal(500UL, reading.SessionBytesSent);
    }

    [Fact]
    public void ElapsedTime_ScalesTheRate()
    {
        var sampler = new ThroughputSampler(smoothing: 1.0);

        sampler.Sample(new AdapterCounters(0, 0, "eth"), T0);
        var reading = sampler.Sample(new AdapterCounters(4_000, 0, "eth"), T0.AddSeconds(4));

        Assert.Equal(1_000, reading.DownloadBytesPerSecond, precision: 6);
    }

    [Fact]
    public void CounterRollback_IsTreatedAsAdapterReset_NotNegativeTraffic()
    {
        var sampler = new ThroughputSampler();

        sampler.Sample(new AdapterCounters(10_000, 10_000, "eth"), T0);
        var reset = sampler.Sample(new AdapterCounters(10, 20, "eth"), T0.AddSeconds(1));

        Assert.Equal(0, reset.DownloadBytesPerSecond);
        Assert.Equal(0, reset.UploadBytesPerSecond);
        Assert.Equal(0UL, reset.SessionBytesReceived);

        var afterReset = sampler.Sample(new AdapterCounters(1_010, 1_020, "eth"), T0.AddSeconds(2));
        Assert.True(afterReset.DownloadBytesPerSecond > 0);
        Assert.Equal(1_000UL, afterReset.SessionBytesReceived);
    }

    [Fact]
    public void SameInstant_DoesNotDivideByZero()
    {
        var sampler = new ThroughputSampler();

        sampler.Sample(new AdapterCounters(100, 100, "eth"), T0);
        var reading = sampler.Sample(new AdapterCounters(200, 200, "eth"), T0);

        Assert.Equal(0, reading.DownloadBytesPerSecond);
        Assert.True(double.IsFinite(reading.UploadBytesPerSecond));
    }

    [Fact]
    public void Reset_ClearsSessionTotals()
    {
        var sampler = new ThroughputSampler();
        sampler.Sample(new AdapterCounters(0, 0, "eth"), T0);
        sampler.Sample(new AdapterCounters(1_000, 1_000, "eth"), T0.AddSeconds(1));

        sampler.Reset();
        var reading = sampler.Sample(new AdapterCounters(5_000, 5_000, "eth"), T0.AddSeconds(2));

        Assert.Equal(0UL, reading.SessionBytesReceived);
        Assert.Equal(0UL, reading.SessionBytesSent);
        Assert.Equal(0, reading.DownloadBytesPerSecond);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void InvalidSmoothing_Throws(double smoothing)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThroughputSampler(smoothing));
    }
}
