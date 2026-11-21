using System.Diagnostics;
using System.Globalization;

namespace DoorNet.Capture;

/// <summary>
/// Resolves a PID to a display name and executable path, cached because the OS lookups
/// are comparatively expensive and PIDs recur every second in the connection table.
/// </summary>
internal sealed class ProcessNameCache
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);
    private readonly Dictionary<int, Entry> _cache = new();

    public (string Name, string? Path) Resolve(int processId)
    {
        if (processId == 0)
            return ("System Idle", null);
        if (processId == 4)
            return ("System", null);

        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(processId, out var entry) && now - entry.ResolvedAt < MaxAge)
            return (entry.Name, entry.Path);

        var resolved = Lookup(processId);
        _cache[processId] = new Entry(resolved.Name, resolved.Path, now);

        if (_cache.Count > 512)
            Prune(now);

        return resolved;
    }

    private void Prune(DateTime now)
    {
        foreach (var key in _cache.Where(kv => now - kv.Value.ResolvedAt > MaxAge * 4).Select(kv => kv.Key).ToList())
            _cache.Remove(key);
    }

    private static (string Name, string? Path) Lookup(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName;

            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                           or InvalidOperationException
                                           or NotSupportedException)
            {
                // Protected process (EDR, anti-cheat, session 0): name only.
            }

            return (FriendlyName(name, path), path);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ($"PID {processId}", null);
        }
    }

    private static string FriendlyName(string processName, string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description) && description.Length <= 60)
                    return description.Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{processName}.exe");
    }

    private readonly record struct Entry(string Name, string? Path, DateTime ResolvedAt);
}
