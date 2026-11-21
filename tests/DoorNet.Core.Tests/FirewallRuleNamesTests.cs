using DoorNet.Core.Firewall;

namespace DoorNet.Core.Tests;

public class FirewallRuleNamesTests
{
    [Fact]
    public void SamePath_ProducesTheSameName()
    {
        var first = FirewallRuleNames.ForPath(@"C:\Program Files\App\app.exe");
        var second = FirewallRuleNames.ForPath(@"C:\Program Files\App\app.exe");

        Assert.Equal(first, second);
    }

    [Fact]
    public void CasingAndWhitespace_DoNotChangeTheName()
    {
        var first = FirewallRuleNames.ForPath(@"C:\Tools\app.exe");
        var second = FirewallRuleNames.ForPath(@"  c:\tools\APP.EXE  ");

        Assert.Equal(first, second);
    }

    [Fact]
    public void SameFileNameInDifferentFolders_GetsDifferentNames()
    {
        var first = FirewallRuleNames.ForPath(@"C:\A\app.exe");
        var second = FirewallRuleNames.ForPath(@"C:\B\app.exe");

        Assert.NotEqual(first, second);
        Assert.Contains("app.exe", first);
        Assert.StartsWith(FirewallRuleNames.Group, first);
    }
}
