using DoorNet.Core.Firewall;

namespace DoorNet.Core.Tests;

public class FirewallStateTests
{
    [Fact]
    public void ParsesPerDirectionEntries()
    {
        var state = FirewallState.Parse("""
            { "Enabled": false, "Blocked": [
              { "Path": "C:\\A\\app.exe", "In": true, "Out": false },
              { "Path": "C:\\B\\app.exe", "In": false, "Out": true } ] }
            """);

        Assert.False(state.Enabled);
        Assert.Equal(2, state.Blocked.Count);
        Assert.True(state.Blocked[0].In);
        Assert.False(state.Blocked[0].Out);
        Assert.False(state.Blocked[1].In);
        Assert.True(state.Blocked[1].Out);
    }

    [Fact]
    public void LegacyBareArray_BlocksBothDirections()
    {
        var state = FirewallState.Parse("""["C:\\Old\\app.exe"]""");

        var entry = Assert.Single(state.Blocked);
        Assert.True(entry.In);
        Assert.True(entry.Out);
        Assert.Equal(@"C:\Old\app.exe", entry.Path);
    }

    [Fact]
    public void LegacyObjectWithPathArray_BlocksBothDirections()
    {
        var state = FirewallState.Parse("""{ "Enabled": true, "Blocked": ["C:\\Old\\app.exe"] }""");

        var entry = Assert.Single(state.Blocked);
        Assert.True(entry.In);
        Assert.True(entry.Out);
    }

    [Fact]
    public void MissingDirectionFlags_DefaultToBlocked()
    {
        var state = FirewallState.Parse("""{ "Blocked": [{ "Path": "C:\\A\\app.exe" }] }""");

        var entry = Assert.Single(state.Blocked);
        Assert.True(entry.In);
        Assert.True(entry.Out);
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
            Blocked =
            [
                new FirewallEntry(@"C:\X\one.exe", In: true, Out: false),
                new FirewallEntry(@"C:\Y\two.exe", In: false, Out: true)
            ]
        };

        var parsed = FirewallState.Parse(original.ToJson());

        Assert.False(parsed.Enabled);
        Assert.Equal(original.Blocked, parsed.Blocked);
    }

    [Fact]
    public void NonStringLegacyEntries_AreIgnored()
    {
        var state = FirewallState.Parse("""["C:\\A\\app.exe", 42, null]""");

        Assert.Single(state.Blocked);
    }

    [Fact]
    public void EntriesWithoutPath_AreIgnored()
    {
        var state = FirewallState.Parse("""{ "Blocked": [{ "In": true, "Out": true }, { "Path": "C:\\B\\ok.exe" }] }""");

        Assert.Single(state.Blocked);
    }
}
