namespace DoorNet.Core.Models;

/// <summary>
/// Per-application activity. Byte counts stay at zero until an elevated per-app usage
/// source (ETW) is attached; connection counts are always available.
/// </summary>
public sealed record AppUsage
{
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public string? ExecutablePath { get; init; }

    public int ConnectionCount { get; init; }
    public int ActiveConnectionCount { get; init; }
    public int RemoteHostCount { get; init; }

    public long BytesSent { get; init; }
    public long BytesReceived { get; init; }

    public bool HasByteUsage => BytesSent > 0 || BytesReceived > 0;

    public string Initial =>
        string.IsNullOrWhiteSpace(ProcessName) ? "?" : char.ToUpperInvariant(ProcessName[0]).ToString();
}
