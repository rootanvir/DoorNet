using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DoorNet.App.Platform;

/// <summary>
/// Resolves public IP addresses to coordinates through ip-api.com (free, no key), one
/// request at a time, with an on-disk cache so a monitoring session does not hammer the
/// service. Private, loopback and reserved addresses are never sent anywhere.
/// </summary>
public sealed class GeoLocator : IDisposable
{
    private const string SelfKey = "__self__";
    private const string Endpoint = "http://ip-api.com/";
    private const string Fields = "fields=status,lat,lon,city,country";

    private static readonly TimeSpan SuccessTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1400);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cachePath;

    private DateTime _lastRequestUtc = DateTime.MinValue;
    private bool _disposed;

    public GeoLocator()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DoorNet");

        _cachePath = Path.Combine(folder, "geo-cache.json");
        LoadCache();
    }

    /// <summary>True for addresses that are safe to look up (public, routable).</summary>
    public static bool IsPublicAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        var text = address.Contains('%')
            ? address[..address.IndexOf('%')]
            : address;

        if (!IPAddress.TryParse(text, out var ip))
            return false;

        if (IPAddress.IsLoopback(ip))
            return false;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal)
                return false;

            var v6 = ip.GetAddressBytes();
            return (v6[0] & 0xFE) != 0xFC; // fc00::/7 unique local
        }

        var b = ip.GetAddressBytes();

        if (b[0] is 0 or 10 or 127 || b[0] >= 224)
            return false;

        if (b[0] == 172 && b[1] is >= 16 and <= 31)
            return false;

        if (b[0] == 192 && b[1] == 168)
            return false;

        if (b[0] == 169 && b[1] == 254)
            return false;

        if (b[0] == 100 && b[1] is >= 64 and <= 127)
            return false;

        if (b[0] == 192 && b[1] == 0 && b[2] == 2)
            return false;

        if (b[0] == 198 && b[1] is 18 or 19)
            return false;

        if (b[0] == 198 && b[1] == 51 && b[2] == 100)
            return false;

        if (b[0] == 203 && b[1] == 0 && b[2] == 113)
            return false;

        return true;
    }

    public Task<GeoPoint?> LocateSelfAsync() => LocateAsync(SelfKey);

    public async Task<GeoPoint?> LocateAsync(string address)
    {
        if (_disposed || string.IsNullOrWhiteSpace(address))
            return null;

        var isSelf = string.Equals(address, SelfKey, StringComparison.Ordinal);
        if (!isSelf && !IsPublicAddress(address))
            return null;

        if (TryGetFresh(address, out var cached))
            return cached;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return null;

            if (TryGetFresh(address, out var inside))
                return inside;

            var wait = MinInterval - (DateTime.UtcNow - _lastRequestUtc);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait).ConfigureAwait(false);

            _lastRequestUtc = DateTime.UtcNow;
            var point = await QueryAsync(isSelf ? $"json/?{Fields}" : $"json/{address}?{Fields}").ConfigureAwait(false);

            _cache[address] = new CacheEntry { Point = point, FetchedUtc = DateTime.UtcNow };
            SaveCache();
            return point;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetFresh(string key, out GeoPoint? point)
    {
        point = null;

        if (!_cache.TryGetValue(key, out var entry))
            return false;

        var ttl = entry.Point is null ? FailureTtl : SuccessTtl;
        if (DateTime.UtcNow - entry.FetchedUtc > ttl)
            return false;

        point = entry.Point;
        return true;
    }

    private async Task<GeoPoint?> QueryAsync(string relative)
    {
        try
        {
            using var response = await _http.GetAsync(Endpoint + relative).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = document.RootElement;

            if (!root.TryGetProperty("status", out var status)
                || !string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("lat", out var lat)
                || !root.TryGetProperty("lon", out var lon))
            {
                return null;
            }

            var city = root.TryGetProperty("city", out var cityElement) ? cityElement.GetString() : null;
            var country = root.TryGetProperty("country", out var countryElement) ? countryElement.GetString() : null;

            return new GeoPoint(lat.GetDouble(), lon.GetDouble(), city, country);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return;

            var entries = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(_cachePath));
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

    private sealed class CacheEntry
    {
        public GeoPoint? Point { get; set; }

        public DateTime FetchedUtc { get; set; }
    }
}
