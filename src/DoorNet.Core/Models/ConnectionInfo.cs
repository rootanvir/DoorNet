namespace DoorNet.Core.Models;

public enum TransportProtocol
{
    Tcp,
    Udp
}

/// <summary>A single entry from the OS connection table.</summary>
public sealed record ConnectionInfo
{
    public required TransportProtocol Protocol { get; init; }
    public required int ProcessId { get; init; }
    public required string LocalAddress { get; init; }
    public required int LocalPort { get; init; }
    public required string RemoteAddress { get; init; }
    public required int RemotePort { get; init; }
    public required string State { get; init; }

    public string ProcessName { get; init; } = "Unknown";
    public string? ExecutablePath { get; init; }

    public bool IsListening => State is "Listen";

    public string RemoteEndpoint => RemotePort == 0
        ? "—"
        : $"{RemoteAddress}:{RemotePort}";

    public string LocalEndpoint => $"{LocalAddress}:{LocalPort}";

    public string ProtocolLabel => Protocol == TransportProtocol.Tcp ? "TCP" : "UDP";

    /// <summary>Stable identity used to detect new/closed connections between snapshots.</summary>
    public string Key =>
        $"{Protocol}|{ProcessId}|{LocalAddress}|{LocalPort}|{RemoteAddress}|{RemotePort}|{State}";
}
