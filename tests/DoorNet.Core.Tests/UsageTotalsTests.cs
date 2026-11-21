using DoorNet.Core.Aggregation;

namespace DoorNet.Core.Tests;

public class UsageTotalsTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AccumulatesPerProcess()
    {
        var totals = new UsageTotals();

        totals.Add(10, "chrome.exe", 1000, 200, Now);
        totals.Add(10, "chrome.exe", 500, 50, Now);

        Assert.Equal((1500L, 250L), totals.Get(10, "chrome.exe"));
    }

    [Fact]
    public void SamePidWithDifferentName_DoesNotShareTotals()
    {
        var totals = new UsageTotals();

        totals.Add(10, "chrome.exe", 1000, 0, Now);
        totals.Add(10, "backup.exe", 7, 0, Now);

        Assert.Equal((1000L, 0L), totals.Get(10, "chrome.exe"));
        Assert.Equal((7L, 0L), totals.Get(10, "backup.exe"));
        Assert.Equal(2, totals.Count);
    }

    [Fact]
    public void UnknownProcess_ReturnsZeros()
    {
        var totals = new UsageTotals();

        Assert.Equal((0L, 0L), totals.Get(99, "missing.exe"));
    }

    [Fact]
    public void NoBytes_DoesNotCreateAnEntry()
    {
        var totals = new UsageTotals();

        totals.Add(10, "idle.exe", 0, 0, Now);

        Assert.Equal(0, totals.Count);
    }

    [Fact]
    public void Clear_ResetsEverything()
    {
        var totals = new UsageTotals();
        totals.Add(10, "chrome.exe", 1000, 200, Now);

        totals.Clear();

        Assert.Equal(0, totals.Count);
        Assert.Equal((0L, 0L), totals.Get(10, "chrome.exe"));
    }

    [Fact]
    public void Prune_KeepsRecentEntriesAndDropsStaleOnes()
    {
        var totals = new UsageTotals();
        var old = Now.AddMinutes(-30);

        totals.Add(1, "old.exe", 100, 0, old);
        totals.Add(2, "new.exe", 100, 0, Now);
        totals.Add(3, "new2.exe", 100, 0, Now);

        totals.Prune(Now, TimeSpan.FromMinutes(10), softLimit: 1);

        Assert.Equal(2, totals.Count);
        Assert.Equal((0L, 0L), totals.Get(1, "old.exe"));
    }

    [Fact]
    public void Prune_DoesNothingBelowSoftLimit()
    {
        var totals = new UsageTotals();
        totals.Add(1, "old.exe", 100, 0, Now.AddDays(-30));

        totals.Prune(Now, TimeSpan.FromMinutes(10), softLimit: 512);

        Assert.Equal(1, totals.Count);
    }
}
