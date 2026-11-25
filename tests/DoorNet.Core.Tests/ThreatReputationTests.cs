using DoorNet.Core.Threats;

namespace DoorNet.Core.Tests;

public sealed class ThreatReputationTests
{
    [Theory]
    [InlineData(0, 0, ThreatSeverity.Low, 0)]
    [InlineData(1, 0, ThreatSeverity.High, 25)]
    [InlineData(5, 0, ThreatSeverity.Critical, 40)]
    [InlineData(12, 3, ThreatSeverity.Critical, 60)]
    [InlineData(0, 4, ThreatSeverity.High, 15)]
    public void VerdictScaleFollowsEngineCounts(int malicious, int suspicious, ThreatSeverity severity, int weight)
    {
        var reputation = new ThreatReputation(
            "paypa1.com", ReputationTargetKind.Domain,
            malicious, suspicious, Harmless: 60, Undetected: 5, [], null, "VirusTotal");

        Assert.Equal(severity, reputation.Severity);
        Assert.Equal(weight, reputation.Weight);
    }

    [Fact]
    public void SummaryReportsEnginesFlagged()
    {
        var reputation = new ThreatReputation(
            "185.220.101.5", ReputationTargetKind.IpAddress,
            12, 2, 70, 10, ["malware", "phishing"], DateTimeOffset.UnixEpoch, "VirusTotal");

        Assert.True(reputation.IsKnown);
        Assert.Equal(94, reputation.Total);
        Assert.Equal("12/94 engines flagged", reputation.Summary);
        Assert.Contains("malware", reputation.Detail);
    }

    [Fact]
    public void UnknownTargetIsNotTreatedAsCleanButAlsoNotFlagged()
    {
        var reputation = new ThreatReputation(
            "nothing.example", ReputationTargetKind.Domain, 0, 0, 0, 0, [], null, "VirusTotal");

        Assert.False(reputation.IsKnown);
        Assert.Equal(0, reputation.Weight);
        Assert.Equal(ThreatSeverity.Low, reputation.Severity);
        Assert.Equal("No reports yet", reputation.Summary);
    }
}
