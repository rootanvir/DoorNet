using DoorNet.Core.Collections;

namespace DoorNet.Core.Tests;

public class RingBufferTests
{
    [Fact]
    public void KeepsInsertionOrderBelowCapacity()
    {
        var buffer = new RingBuffer<int>(4);

        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        Assert.Equal([1, 2, 3], buffer.ToArray());
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void OverwritesOldestWhenFull()
    {
        var buffer = new RingBuffer<int>(3);

        for (var i = 1; i <= 5; i++)
            buffer.Add(i);

        Assert.Equal(3, buffer.Count);
        Assert.Equal([3, 4, 5], buffer.ToArray());
        Assert.Equal(3, buffer[0]);
        Assert.Equal(5, buffer[2]);
    }

    [Fact]
    public void Clear_EmptiesAndAllowsReuse()
    {
        var buffer = new RingBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);

        buffer.Clear();
        buffer.Add(9);

        Assert.Equal(1, buffer.Count);
        Assert.Equal([9], buffer.ToArray());
    }

    [Fact]
    public void Indexer_RejectsOutOfRange()
    {
        var buffer = new RingBuffer<int>(2);
        buffer.Add(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[-1]);
    }

    [Fact]
    public void MinimumCapacity_IsEnforced()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBuffer<int>(0));
    }
}
