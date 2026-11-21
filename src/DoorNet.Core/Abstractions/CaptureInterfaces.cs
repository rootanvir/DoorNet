using DoorNet.Core.Models;

namespace DoorNet.Core.Abstractions;

/// <summary>Aggregate adapter throughput. Implemented over OS interface counters.</summary>
public interface IThroughputSource
{
    /// <summary>Human-readable description of the adapters being sampled.</summary>
    string Describe();

    /// <summary>Reads raw counters. Safe to call once per tick; never throws.</summary>
    AdapterCounters ReadCounters();
}

/// <summary>Snapshot of the OS TCP/UDP tables.</summary>
public interface IConnectionSource
{
    /// <summary>Returns the current connections, grouped nowhere. Never throws.</summary>
    IReadOnlyList<ConnectionInfo> Snapshot();
}

/// <summary>
/// Per-application byte usage. Requires an elevated kernel ETW session, so the default
/// implementation reports why it is unavailable and the UI degrades gracefully.
/// </summary>
public interface IPerAppUsageSource
{
    bool IsAvailable { get; }

    /// <summary>Human-readable explanation when <see cref="IsAvailable"/> is false.</summary>
    string? UnavailableReason { get; }

    /// <summary>Bytes accumulated since the previous call, keyed by process id.</summary>
    IReadOnlyList<PerAppTraffic> Drain();
}
