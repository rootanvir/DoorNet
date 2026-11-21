namespace DoorNet.Core.Models;

/// <summary>
/// Per-process byte usage for one sampling interval. Byte counts are deltas since the
/// previous <c>Drain</c>, not lifetime totals.
/// </summary>
public readonly record struct PerAppTraffic(
    int ProcessId,
    long BytesSent,
    long BytesReceived,
    string? ProcessName = null,
    string? ExecutablePath = null);
