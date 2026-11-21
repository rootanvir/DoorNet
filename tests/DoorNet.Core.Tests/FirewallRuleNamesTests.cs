using DoorNet.Core.Firewall;

namespace DoorNet.Core.Tests;

public class FirewallRuleNamesTests
{
    [Fact]
    public void SamePath_ProducesTheSameName()
    {
        var first = FirewallRuleNames.ForPath(@"C:\Program Files\App\app.exe", FirewallDirection.Out);
        var second = FirewallRuleNames.ForPath(@"C:\Program Files\App\app.exe", FirewallDirection.Out);

        Assert.Equal(first, second);
    }

    [Fact]
    public void CasingAndWhitespace_DoNotChangeTheName()
    {
        var first = FirewallRuleNames.ForPath(@"C:\Tools\app.exe", FirewallDirection.In);
        var second = FirewallRuleNames.ForPath(@"  c:\tools\APP.EXE  ", FirewallDirection.In);

        Assert.Equal(first, second);
    }

    [Fact]
    public void SameFileNameInDifferentFolders_GetsDifferentNames()
    {
        var first = FirewallRuleNames.ForPath(@"C:\A\app.exe", FirewallDirection.Out);
        var second = FirewallRuleNames.ForPath(@"C:\B\app.exe", FirewallDirection.Out);

        Assert.NotEqual(first, second);
        Assert.Contains("app.exe", first);
        Assert.StartsWith(FirewallRuleNames.Group, first);
    }

    [Fact]
    public void Directions_AreDistinct()
    {
        var inbound = FirewallRuleNames.ForPath(@"C:\A\app.exe", FirewallDirection.In);
        var outbound = FirewallRuleNames.ForPath(@"C:\A\app.exe", FirewallDirection.Out);

        Assert.NotEqual(inbound, outbound);
        Assert.EndsWith(" in", inbound);
        Assert.EndsWith(" out", outbound);
    }

    [Fact]
    public void LegacyName_HasNoDirectionSuffix()
    {
        var legacy = FirewallRuleNames.ForPath(@"C:\A\app.exe");

        Assert.DoesNotContain(" out", legacy);
        Assert.DoesNotContain(" in", legacy);
    }
}
