using System.Text.Json;

namespace DoorNet.Core.Firewall;

/// <summary>
/// Persisted firewall state: the master switch plus the executables DoorNet has blocked.
/// Parsing also accepts the pre-master-switch format (a bare JSON array of paths).
/// </summary>
public sealed class FirewallState
{
    public bool Enabled { get; init; } = true;

    public IReadOnlyList<string> Blocked { get; init; } = Array.Empty<string>();

    public static FirewallState Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new FirewallState();

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
                return new FirewallState { Blocked = ReadPaths(root) };

            if (root.ValueKind != JsonValueKind.Object)
                return new FirewallState();

            // Missing "Enabled" means an older object file; default to enabled.
            var enabled = !root.TryGetProperty("Enabled", out var enabledElement)
                          || enabledElement.ValueKind != JsonValueKind.False;

            var blocked = root.TryGetProperty("Blocked", out var blockedElement)
                          && blockedElement.ValueKind == JsonValueKind.Array
                ? (IReadOnlyList<string>)ReadPaths(blockedElement)
                : Array.Empty<string>();

            return new FirewallState { Enabled = enabled, Blocked = blocked };
        }
        catch (JsonException)
        {
            return new FirewallState();
        }
    }

    public string ToJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    private static List<string> ReadPaths(JsonElement array)
    {
        var paths = new List<string>(array.GetArrayLength());

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
                paths.Add(value);
        }

        return paths;
    }
}
