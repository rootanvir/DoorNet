namespace DoorNet.Core.Firewall;

public enum FirewallChangeStatus
{
    Success,
    NotElevated,
    InvalidPath,
    Unsupported,
    Failed
}

public readonly record struct FirewallChangeResult(FirewallChangeStatus Status, string Message)
{
    public bool Succeeded => Status == FirewallChangeStatus.Success;

    public static FirewallChangeResult Ok(string message) => new(FirewallChangeStatus.Success, message);

    public static FirewallChangeResult Fail(FirewallChangeStatus status, string message) => new(status, message);
}

/// <summary>
/// Per-application internet blocking. Implementations add/remove operating-system firewall
/// rules; they never touch packet contents and never require a kernel driver of their own.
/// </summary>
public interface IFirewallController
{
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    /// <summary>Master switch. When false, DoorNet's rules stay defined but are disabled.</summary>
    bool IsEnabled { get; }

    /// <summary>Executable paths currently blocked, normalised to full paths.</summary>
    IReadOnlyCollection<string> BlockedPaths { get; }

    bool IsBlocked(string? executablePath);

    bool IsBlocked(string? executablePath, FirewallDirection direction);

    /// <summary>Turns every DoorNet rule on or off without losing the blocked list.</summary>
    FirewallChangeResult SetEnabled(bool enabled);

    /// <summary>Blocks or allows one direction for an executable.</summary>
    FirewallChangeResult SetBlocked(string? executablePath, FirewallDirection direction, bool blocked);

    FirewallChangeResult Block(string? executablePath);

    FirewallChangeResult Allow(string? executablePath);
}
