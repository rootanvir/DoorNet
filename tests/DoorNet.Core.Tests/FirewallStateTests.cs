using DoorNet.Core.Firewall;

namespace DoorNet.Core.Tests;

public class FirewallStateTests
{
    [Fact]
    public void ParsesCurrentFormat()
    {
        var state = FirewallState.Parse("""
            { "Enabled": false, "Blocked": ["C:\\A\\app.exe", "C:\\B\\app.exe"] }
            """);

        Assert.False(state.Enabled);
        Assert.Equal(2, state.Blocked.Count);
        Assert.Contains(@"C:\A\app.exe", state.Blocked);
    }

    [Fact]
    public void ParsesLegacyBareArrayAsEnabled()
    {
        var state = FirewallState.Parse("""["C:\\Old\\app.exe"]""");

        Assert.True(state.Enabled);
        Assert.Single(state.Blocked);
        Assert.Equal(@"C:\Old\app.exe", state.Blocked[0]);
    }

    [Fact]
    public void MissingEnabled_DefaultsToTrue()
    {
        var state = FirewallState.Parse("""{ "Blocked": [] }""");

        Assert.True(state.Enabled);
        Assert.Empty(state.Blocked);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{} trailing")]
    public void MalformedInput_FallsBackToDefaults(string json)
    {
        var state = FirewallState.Parse(json);

        Assert.True(state.Enabled);
        Assert.Empty(state.Blocked);
    }

    [Fact]
    public void RoundTrips()
    {
        var original = new FirewallState
        {
            Enabled = false,
            Blocked = [@"C:\X\one.exe", @"C:\Y\two.exe"]
        };

        var parsed = FirewallState.Parse(original.ToJson());

        Assert.False(parsed.Enabled);
        Assert.Equal(original.Blocked, parsed.Blocked);
    }

    [Fact]
    public void NonStringEntries_AreIgnored()
    {
        var state = FirewallState.Parse("""["C:\\A\\app.exe", 42, null]""");

        Assert.Single(state.Blocked);
    }
}
