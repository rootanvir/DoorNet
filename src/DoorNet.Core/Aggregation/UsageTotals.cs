using DoorNet.Core.Models;

namespace DoorNet.Core.Aggregation;

/// <summary>
/// Accumulates per-application byte totals for the current session. Keyed by process id
/// *and* image name so a recycled PID does not inherit another program's history.
/// </summary>
public sealed class UsageTotals
{
    private readonly Dictionary<Key, Entry> _entries = new();

    public int Count => _entries.Count;

    public void Add(AppUsage usage, DateTime utcNow) =>
        Add(usage.ProcessId, usage.ProcessName, usage.BytesReceived, usage.BytesSent, utcNow);

    public void Add(int processId, string processName, long downloadBytes, long uploadBytes, DateTime utcNow)
    {
        if (downloadBytes <= 0 && uploadBytes <= 0)
            return;

        var key = new Key(processId, processName ?? string.Empty);
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry();
            _entries[key] = entry;
        }

        entry.Download += Math.Max(0, downloadBytes);
        entry.Upload += Math.Max(0, uploadBytes);
        entry.LastSeenUtc = utcNow;
    }

    public (long Download, long Upload) Get(int processId, string processName)
    {
        var key = new Key(processId, processName ?? string.Empty);
        return _entries.TryGetValue(key, out var entry)
            ? (entry.Download, entry.Upload)
            : (0, 0);
    }

    public void Clear() => _entries.Clear();

    /// <summary>
    /// Drops entries idle for longer than <paramref name="maxAge"/> once the table grows
    /// past <paramref name="softLimit"/>, so long sessions with heavy PID churn stay bounded.
    /// </summary>
    public void Prune(DateTime utcNow, TimeSpan maxAge, int softLimit = 512)
    {
        if (_entries.Count <= softLimit)
            return;

        var cutoff = utcNow - maxAge;
        var stale = _entries
            .Where(pair => pair.Value.LastSeenUtc < cutoff)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in stale)
            _entries.Remove(key);
    }

    private readonly record struct Key(int ProcessId, string ProcessName);

    private sealed class Entry
    {
        public long Download;
        public long Upload;
        public DateTime LastSeenUtc;
    }
}
