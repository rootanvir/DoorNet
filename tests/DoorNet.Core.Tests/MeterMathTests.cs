using DoorNet.Core.Formatting;

namespace DoorNet.Core.Tests;

public class MeterMathTests
{
    [Theory]
    [InlineData(0, 1000)]
    [InlineData(-5, 1000)]
    [InlineData(100, 0)]
    [InlineData(double.NaN, 1000)]
    [InlineData(100, double.NaN)]
    public void NothingToDraw_ReturnsZero(double rate, double max)
    {
        Assert.Equal(0, MeterMath.Scale(rate, max));
    }

    [Fact]
    public void BusiestRow_FillsTheBar()
    {
        Assert.Equal(1, MeterMath.Scale(1000, 1000), precision: 6);
    }

    [Fact]
    public void HalfOfMax_IsSquareRootOfHalf()
    {
        Assert.Equal(Math.Sqrt(0.5), MeterMath.Scale(500, 1000), precision: 6);
    }

    [Fact]
    public void TinyRates_StayVisible()
    {
        Assert.Equal(MeterMath.MinimumVisible, MeterMath.Scale(1, 100_000_000), precision: 6);
    }

    [Fact]
    public void RateAboveMax_IsClamped()
    {
        Assert.Equal(1, MeterMath.Scale(5000, 1000), precision: 6);
    }
}
