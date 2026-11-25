using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using DoorNet.Core.Threats;

namespace DoorNet.App.Platform;

/// <summary>
/// VirusTotal lookups for suspicious hosts. Opt-in: without a key it reports itself
/// unavailable and the app keeps running on local heuristics. Results are cached for a day
/// and requests are spaced out so the free tier (4 per minute) is never exceeded.
/// </summary>
public sealed class VirusTotalClient : IThreatReputationSource, IDisposable
{
    private const string BaseAddress = "https://www.virustotal.com/api/v3/";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan UnknownLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(16);

    private readonly HttpClient _http = new() { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(15) };
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private string? _failure;

    public VirusTotalClient()
    {
        Configure(SettingsStore.Load().VirusTotalApiKey);
        LoadCache();
    }

    /// <summary>
    /// Re-reads the key. Precedence: a key entered in the app, then the environment, then .env.
    /// Passing null/empty falls back to the file/environment lookup.
    /// </summary>
    public void Configure(string? keyFromApp)
    {
        var resolved = string.IsNullOrWhiteSpace(keyFromApp)
            ? DotEnv.Get("VIRUSTOTAL_API_KEY")
            : keyFromApp.Trim();

        Key = resolved;

        _http.DefaultRequestHeaders.Remove("x-apikey");
        if (!string.IsNullOrWhiteSpace(resolved))
            _http.DefaultRequestHeaders.Add("x-apikey", resolved);

        UnavailableReason = string.IsNullOrWhiteSpace(resolved)
            ? "No key yet: paste one below, or set VIRUSTOTAL_API_KEY in .env."
            : null;

        _failure = null;
    }

    public string? Key { get; private set; }

    public bool IsAvailable => UnavailableReason is null;

    public string? UnavailableReason { get; private set; }

    /// <summary>"VirusTotal" with a masked key hint, safe to show in the UI.</summary>
    public string StatusText => IsAvailable
        ? $"VirusTotal enabled · key ending {Mask(Key)}"
        : _failure ?? UnavailableReason ?? "Not configured";

    public async Task<ThreatReputation?> LookupAsync(string target, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(target))
            return null;

        var (kind, path, normalized) = Classify(target);
        if (kind is null)
            return null;

        if (TryGetFresh(normalized, out var cached))
            return cached;

        if (!IsAvailable)
            return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = MinimumInterval - (DateTimeOffset.Now - _lastRequest);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);

            using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
            _lastRequest = DateTimeOffset.Now;

            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                Store(normalized, null, UnknownLifetime);
                return null;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                UnavailableReason = "VirusTotal rejected the key (401/403).";
                _failure = UnavailableReason;
                return null;
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests)
            {
                _failure = "VirusTotal rate limit hit; will retry later.";
                return null;
            }

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var reputation = Parse(json, normalized, kind.Value);
            Store(normalized, reputation, CacheLifetime);
            SaveCache();
            return reputation;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _failure = "VirusTotal unreachable.";
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _http.Dispose();

    private static (ReputationTargetKind? Kind, string Path, string Target) Classify(string target)
    {
        var value = target.Trim().Trim('.');
        if (value.Length == 0)
            return (null, string.Empty, string.Empty);

        if (IPAddress.TryParse(value, out var address))
        {
            // Never send the private side of the network to a third party.
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                if (bytes[0] is 10 or 127
                    || (bytes[0] == 192 && bytes[1] == 168)
                    || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31))
                    return (null, string.Empty, string.Empty);
            }

            return (ReputationTargetKind.IpAddress, $"ip_addresses/{value}", value);
        }

        return (ReputationTargetKind.Domain, $"domains/{value}", value);
    }

    private static ThreatReputation Parse(string json, string target, ReputationTargetKind kind)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("attributes", out var attributes))
        {
            return new ThreatReputation(target, kind, 0, 0, 0, 0, [], null, "VirusTotal");
        }

        var malicious = 0;
        var suspicious = 0;
        var harmless = 0;
        var undetected = 0;

        if (attributes.TryGetProperty("last_analysis_stats", out var stats))
        {
            malicious = Read(stats, "malicious");
            suspicious = Read(stats, "suspicious");
            harmless = Read(stats, "harmless");
            undetected = Read(stats, "undetected");
        }

        var categories = new List<string>();
        if (attributes.TryGetProperty("categories", out var categoryMap) && categoryMap.ValueKind == JsonValueKind.Object)
        {
            foreach (var category in categoryMap.EnumerateObject())
            {
                var name = category.Value.GetString();
                if (!string.IsNullOrWhiteSpace(name) && !categories.Contains(name, StringComparer.OrdinalIgnoreCase))
                    categories.Add(name);
            }
        }

        DateTimeOffset? lastAnalysis = null;
        if (attributes.TryGetProperty("last_analysis_date", out var stamp) && stamp.TryGetInt64(out var seconds))
            lastAnalysis = DateTimeOffset.FromUnixTimeSeconds(seconds);

        return new ThreatReputation(
            target, kind, malicious, suspicious, harmless, undetected,
            categories, lastAnalysis, "VirusTotal");
    }

    private static int Read(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private bool TryGetFresh(string target, out ThreatReputation? reputation)
    {
        reputation = null;
        if (!_cache.TryGetValue(target, out var entry))
            return false;

        var lifetime = entry.Reputation is null ? UnknownLifetime : CacheLifetime;
        if (DateTimeOffset.Now - entry.Fetched > lifetime)
            return false;

        reputation = entry.Reputation;
        return true;
    }

    private void Store(string target, ThreatReputation? reputation, TimeSpan lifetime)
    {
        _cache[target] = new CacheEntry(reputation, DateTimeOffset.Now, lifetime);
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
                return;

            var entries = JsonSerializer.Deserialize<Dictionary<string, StoredEntry>>(File.ReadAllText(CachePath));
            if (entries is null)
                return;

            foreach (var (key, stored) in entries)
                _cache[key] = new CacheEntry(stored.Reputation, stored.Fetched, CacheLifetime);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt cache is not worth failing over.
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);

            var entries = _cache
                .Where(pair => DateTimeOffset.Now - pair.Value.Fetched < CacheLifetime)
                .ToDictionary(
                    pair => pair.Key,
                    pair => new StoredEntry(pair.Value.Reputation, pair.Value.Fetched),
                    StringComparer.OrdinalIgnoreCase);

            File.WriteAllText(CachePath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Mask(string? key) =>
        string.IsNullOrEmpty(key) || key.Length < 6 ? "…" : "…" + key[^4..];

    private static string CacheFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoorNet");

    private static string CachePath => Path.Combine(CacheFolder, "vt-cache.json");

    private sealed record CacheEntry(ThreatReputation? Reputation, DateTimeOffset Fetched, TimeSpan Lifetime);

    private sealed record StoredEntry(ThreatReputation? Reputation, DateTimeOffset Fetched);
}
