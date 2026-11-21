using DoorNet.Core.Formatting;

namespace DoorNet.Core.Tests;

public class ByteFormatterTests
{
    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(-5, "0 B/s")]
    [InlineData(double.NaN, "0 B/s")]
    [InlineData(512, "512 B/s")]
    [InlineData(1024, "1.00 KB/s")]
    [InlineData(1_572_864, "1.50 MB/s")]
    [InlineData(150_000_000, "143 MB/s")]
    public void FormatRate_IsReadable(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, ByteFormatter.FormatRate(bytesPerSecond));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(5_368_709_120, "5.00 GB")]
    public void FormatBytes_ScalesUnits(double bytes, string expected)
    {
        Assert.Equal(expected, ByteFormatter.FormatBytes(bytes));
    }

    [Fact]
    public void SplitRate_ReturnsValueAndUnitSeparately()
    {
        var (value, unit) = ByteFormatter.SplitRate(2_500_000);

        Assert.Equal("2.38", value);
        Assert.Equal("MB/s", unit);
    }

    [Fact]
    public void FormatCount_AddsThousandsSeparators()
    {
        Assert.Equal("1,158", ByteFormatter.FormatCount(1158));
    }
}

public class ChartMathTests
{
    [Theory]
    [InlineData(0, 4096)]
    [InlineData(1000, 4096)]
    [InlineData(5000, 5000)]
    [InlineData(12_000, 20_000)]
    [InlineData(2_500_000, 2_500_000)]
    public void NiceMax_NeverDropsBelowTheFloor(double peak, double expected)
    {
        Assert.Equal(expected, ChartMath.NiceMax(peak));
    }

    [Theory]
    [InlineData(1500, 2000)]
    [InlineData(2300, 2500)]
    [InlineData(4000, 5000)]
    public void NiceMax_RoundsUpToReadableSteps(double peak, double expected)
    {
        Assert.Equal(expected, ChartMath.NiceMax(peak, floor: 0));
    }
}
