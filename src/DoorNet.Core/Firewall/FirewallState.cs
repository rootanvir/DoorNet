using System.Text.Json;

namespace DoorNet.Core.Firewall;

/// <summary>One blocked executable and which directions are blocked.</summary>
public sealed record FirewallEntry(string Path, bool In, bool Out)
{
    public bool IsBlocked(FirewallDirection direction) =>
        direction == FirewallDirection.In ? In : Out;
}

/// <summary>
/// Persisted firewall state: the master switch plus per-executable direction flags.
/// Parsing also accepts the older formats (bare path arrays, or paths blocked both ways).
/// </summary>
public sealed class FirewallState
{
    public bool Enabled { get; init; } = true;

    public IReadOnlyList<FirewallEntry> Blocked { get; init; } = Array.Empty<FirewallEntry>();

    public static FirewallState Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new FirewallState();

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
                return new FirewallState { Blocked = ReadEntries(root) };

            if (root.ValueKind != JsonValueKind.Object)
                return new FirewallState();

            // Missing "Enabled" means an older object file; default to enabled.
            var enabled = !root.TryGetProperty("Enabled", out var enabledElement)
                          || enabledElement.ValueKind != JsonValueKind.False;

            var blocked = root.TryGetProperty("Blocked", out var blockedElement)
                          && blockedElement.ValueKind == JsonValueKind.Array
                ? (IReadOnlyList<FirewallEntry>)ReadEntries(blockedElement)
                : Array.Empty<FirewallEntry>();

            return new FirewallState { Enabled = enabled, Blocked = blocked };
        }
        catch (JsonException)
        {
            return new FirewallState();
        }
    }

    public string ToJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    private static List<FirewallEntry> ReadEntries(JsonElement array)
    {
        var entries = new List<FirewallEntry>(array.GetArrayLength());

        foreach (var item in array.EnumerateArray())
        {
            // Legacy: a bare path meant "blocked in both directions".
            if (item.ValueKind == JsonValueKind.String)
            {
                if (item.GetString() is { Length: > 0 } legacy)
                    entries.Add(new FirewallEntry(legacy, In: true, Out: true));

                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (!item.TryGetProperty("Path", out var pathElement)
                || pathElement.ValueKind != JsonValueKind.String
                || pathElement.GetString() is not { Length: > 0 } path)
            {
                continue;
            }

            var inbound = !item.TryGetProperty("In", out var inElement) || inElement.ValueKind != JsonValueKind.False;
            var outbound = !item.TryGetProperty("Out", out var outElement) || outElement.ValueKind != JsonValueKind.False;

            entries.Add(new FirewallEntry(path, inbound, outbound));
        }

        return entries;
    }
}
