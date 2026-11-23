using System.IO;
using System.Net.Http;
using System.Text.Json;
using DoorNet.Core.Scanning;

namespace DoorNet.App.Platform;

/// <summary>
/// Best-effort vendor lookup for MAC prefixes missing from the offline table. Only the six
/// hex digits of the OUI are sent to api.macvendors.com, one request per 1.2 s (their free
/// tier), and every answer - including "not found" - is cached on disk. Randomised
/// (locally administered) MACs are never queried because they have no registered vendor.
/// </summary>
public sealed class MacVendorLookup : IDisposable
{
    private const string Endpoint = "https://api.macvendors.com/";

    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan FoundTtl = TimeSpan.FromDays(90);
    private static readonly TimeSpan MissingTtl = TimeSpan.FromDays(7);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cachePath;

    private DateTime _lastRequestUtc = DateTime.MinValue;
    private bool _disposed;

    public MacVendorLookup()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DoorNet/0.1");

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DoorNet");

        _cachePath = Path.Combine(folder, "oui-api-cache.json");
        LoadCache();
    }

    public async Task<string?> LookupAsync(string mac, CancellationToken cancellationToken = default)
    {
        var prefix = MacAddress.Prefix(mac);
        if (prefix.Length != 6 || MacAddress.IsLocallyAdministered(prefix))
            return null;

        if (TryGetFresh(prefix, out var cached))
            return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
                return null;

            if (TryGetFresh(prefix, out var inside))
                return inside;

            var wait = MinInterval - (DateTime.UtcNow - _lastRequestUtc);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);

            _lastRequestUtc = DateTime.UtcNow;
            string? vendor = null;

            try
            {
                using var response = await _http.GetAsync(Endpoint + prefix, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var text = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
                    if (text.Length is > 0 and <= 120)
                        vendor = text;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // Offline or rate-limited: remember the miss briefly and move on.
            }

            _cache[prefix] = new Entry { Vendor = vendor, FetchedUtc = DateTime.UtcNow };
            SaveCache();
            return vendor;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetFresh(string prefix, out string? vendor)
    {
        vendor = null;

        if (!_cache.TryGetValue(prefix, out var entry))
            return false;

        var ttl = entry.Vendor is null ? MissingTtl : FoundTtl;
        if (DateTime.UtcNow - entry.FetchedUtc > ttl)
            return false;

        vendor = entry.Vendor;
        return true;
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return;

            var entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_cachePath));
            if (entries is null)
                return;

            foreach (var (key, entry) in entries)
                _cache[key] = entry;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _http.Dispose();
        _gate.Dispose();
    }

    private sealed class Entry
    {
        public string? Vendor { get; set; }

        public DateTime FetchedUtc { get; set; }
    }
}
