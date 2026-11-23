using DoorNet.Core.Collections;

namespace DoorNet.Core.Tests;

public class PagerTests
{
    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(1, 10, 1)]
    [InlineData(10, 10, 1)]
    [InlineData(11, 10, 2)]
    [InlineData(23, 10, 3)]
    public void PageCount_RoundsUp(int items, int pageSize, int expected)
    {
        Assert.Equal(expected, Pager.PageCount(items, pageSize));
    }

    [Theory]
    [InlineData(-3, 23, 10, 0)]
    [InlineData(0, 23, 10, 0)]
    [InlineData(2, 23, 10, 2)]
    [InlineData(9, 23, 10, 2)]
    public void ClampPage_StaysInRange(int page, int items, int pageSize, int expected)
    {
        Assert.Equal(expected, Pager.ClampPage(page, items, pageSize));
    }

    [Fact]
    public void Slice_ReturnsOnePage()
    {
        var items = Enumerable.Range(1, 23).ToList();

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], Pager.Slice(items, 0, 10));
        Assert.Equal([11, 12, 13, 14, 15, 16, 17, 18, 19, 20], Pager.Slice(items, 1, 10));
        Assert.Equal([21, 22, 23], Pager.Slice(items, 2, 10));
    }

    [Fact]
    public void Slice_HandlesEmpty()
    {
        Assert.Empty(Pager.Slice(Array.Empty<int>(), 0, 10));
    }

    [Theory]
    [InlineData(0, 23, 10, "1-10 of 23")]
    [InlineData(1, 23, 10, "11-20 of 23")]
    [InlineData(2, 23, 10, "21-23 of 23")]
    [InlineData(0, 2, 10, "1-2 of 2")]
    public void RangeText_DescribesTheWindow(int page, int items, int pageSize, string expected)
    {
        Assert.Equal(expected, Pager.RangeText(page, items, pageSize));
    }
}
