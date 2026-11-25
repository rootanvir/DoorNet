namespace DoorNet.Core.Threats;

public enum ReputationTargetKind
{
    Domain,
    IpAddress,
    File
}

/// <summary>Reputation for one host, address or file, as reported by an external service.</summary>
public sealed record ThreatReputation(
    string Target,
    ReputationTargetKind Kind,
    int Malicious,
    int Suspicious,
    int Harmless,
    int Undetected,
    IReadOnlyList<string> Categories,
    DateTimeOffset? LastAnalysis,
    string Source)
{
    public int Total => Malicious + Suspicious + Harmless + Undetected;

    public bool IsKnown => Total > 0;

    public string Summary => !IsKnown
        ? "No reports yet"
        : $"{Malicious}/{Total} engines flagged";

    public string Detail
    {
        get
        {
            if (Categories.Count > 0)
                return string.Join(", ", Categories.Take(3));

            return LastAnalysis is { } stamp
                ? $"last analysed {stamp:yyyy-MM-dd}"
                : "No categories reported";
        }
    }

    public ThreatSeverity Severity => Malicious >= 5 ? ThreatSeverity.Critical
        : Malicious >= 1 || Suspicious >= 3 ? ThreatSeverity.High
        : Suspicious >= 1 ? ThreatSeverity.Medium
        : ThreatSeverity.Low;

    /// <summary>Points this reputation would add to a local score.</summary>
    public int Weight => Malicious >= 10 ? 60
        : Malicious >= 5 ? 40
        : Malicious >= 1 ? 25
        : Suspicious >= 2 ? 15
        : 0;
}

/// <summary>
/// Optional reputation lookups (VirusTotal). Implementations must never block the UI and
/// must tolerate being unavailable: DoorNet works from local heuristics alone.
/// </summary>
public interface IThreatReputationSource
{
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    Task<ThreatReputation?> LookupAsync(string target, CancellationToken cancellationToken = default);
}
