namespace DoorNet.Core.Models;

/// <summary>
/// Monotonic byte counters read from the OS. Values are totals since boot (or since the
/// adapter was reset), never deltas.
/// </summary>
public readonly record struct AdapterCounters(
    ulong BytesReceived,
    ulong BytesSent,
    string AdapterSummary)
{
    public static AdapterCounters Empty { get; } = new(0, 0, "No active adapter");
}
