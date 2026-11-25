using DoorNet.Core.Threats;

namespace DoorNet.Core.Tests;

public sealed class UrlCategoryTests
{
    [Theory]
    [InlineData(100, UrlCategory.Danger)]
    [InlineData(75, UrlCategory.Danger)]
    [InlineData(74, UrlCategory.Suspicious)]
    [InlineData(50, UrlCategory.Suspicious)]
    [InlineData(49, UrlCategory.Watch)]
    [InlineData(30, UrlCategory.Watch)]
    [InlineData(29, UrlCategory.Safe)]
    [InlineData(0, UrlCategory.Safe)]
    public void ScoreBandsMapToVerdictTags(int score, UrlCategory expected)
    {
        Assert.Equal(expected, UrlCategories.For(score));
    }

    [Fact]
    public void ExemptAddressIsAlwaysSafe()
    {
        var local = UrlAnalyzer.Assess("http://192.168.0.1/admin");

        Assert.True(local.IsExempt);
        Assert.Equal(UrlCategory.Safe, UrlCategories.For(local));
    }

    [Fact]
    public void LabelsReadAsUserFacingTags()
    {
        Assert.Equal("Danger", UrlCategories.Label(UrlCategory.Danger));
        Assert.Equal("Suspicious", UrlCategories.Label(UrlCategory.Suspicious));
        Assert.Equal("Watch", UrlCategories.Label(UrlCategory.Watch));
        Assert.Equal("Safe", UrlCategories.Label(UrlCategory.Safe));
    }

    [Fact]
    public void DangerSortsAboveSafe()
    {
        Assert.True(UrlCategories.Rank(UrlCategory.Danger) < UrlCategories.Rank(UrlCategory.Suspicious));
        Assert.True(UrlCategories.Rank(UrlCategory.Suspicious) < UrlCategories.Rank(UrlCategory.Watch));
        Assert.True(UrlCategories.Rank(UrlCategory.Watch) < UrlCategories.Rank(UrlCategory.Safe));
    }
}
