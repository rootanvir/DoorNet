using System.Globalization;
using System.Net;
using System.Text;

namespace DoorNet.Core.Threats;

public enum ThreatSeverity
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>One warning sign found in a URL, with the weight it contributed.</summary>
public sealed record ThreatSignal(string Id, int Weight, string Description);

/// <summary>Outcome of scoring one URL. <see cref="Signals"/> explains the score.</summary>
public sealed record UrlAssessment(
    string Url,
    string Host,
    int Score,
    ThreatSeverity Severity,
    bool IsExempt,
    IReadOnlyList<ThreatSignal> Signals)
{
    public bool IsSuspicious => !IsExempt && Score >= UrlAnalyzer.SuspiciousScore;

    public string Reasons => Signals.Count == 0
        ? "No warning signs"
        : string.Join(" · ", Signals.Select(signal => signal.Description));

    public string SeverityText => Severity switch
    {
        ThreatSeverity.Critical => "Critical",
        ThreatSeverity.High => "High",
        ThreatSeverity.Medium => "Medium",
        _ => "Low"
    };
}

/// <summary>A detector, listed in the Rules tab so the scoring stays explainable.</summary>
public sealed record SignalRule(string Id, string Title, int Weight, string Detail);

/// <summary>
/// Scores a URL from its shape alone: no network calls, no reputation service. The result is
/// advisory — it highlights patterns that phishing and malware lures share, and stays quiet
/// for ordinary sites.
/// </summary>
public static class UrlAnalyzer
{
    /// <summary>At or above this score the URL is reported to the Alerts tab.</summary>
    public const int SuspiciousScore = 50;

    public const int CriticalScore = 75;
    public const int MediumScore = 30;

    /// <summary>Every detector, with its weight. The Rules tab renders this list.</summary>
    public static IReadOnlyList<SignalRule> Rules { get; } =
    [
        new("ip-host", "Direct IP address", 30, "The host is a raw address instead of a name."),
        new("userinfo", "Credentials trick", 55, "Text before '@' disguises the real host."),
        new("idn", "Non-Latin characters", 30, "Host uses internationalised (punycode) characters."),
        new("mixed-script", "Mixed alphabets", 15, "Host mixes Latin with Cyrillic, Greek or Arabic letters."),
        new("brand-lookalike", "Brand lookalike", 55, "Registered name is one or two letters away from a known brand."),
        new("brand-subdomain", "Brand only in subdomain", 50, "A brand name sits in a subdomain while the real domain is different."),
        new("bad-tld", "Abused top-level domain", 15, "Top-level domain is commonly used for throwaway lures."),
        new("ddns", "Dynamic DNS host", 40, "Host lives on a free dynamic-DNS provider."),
        new("http-login", "Unencrypted login page", 20, "Plain HTTP page whose path asks for credentials."),
        new("double-extension", "Disguised executable", 55, "File name hides an executable behind a document extension."),
        new("random-label", "Random-looking host", 15, "Host label is long and high-entropy, typical of generated domains."),
        new("deep-subdomain", "Deep subdomain", 10, "Four or more labels before the registered domain."),
        new("odd-port", "Uncommon port", 15, "Port is not 80 or 443."),
        new("encoded-path", "Encoded path", 10, "Very long path with a base64-like blob."),
        new("temp-process", "Runs from a temp folder", 20, "Process lives in Temp or Downloads, where dropped files land.")
    ];

    private static readonly string[] Brands =
    [
        "microsoft", "windows", "office", "outlook", "onedrive", "google", "gmail", "youtube",
        "apple", "icloud", "paypal", "amazon", "netflix", "facebook", "instagram", "whatsapp",
        "telegram", "discord", "steam", "binance", "coinbase", "metamask", "dropbox", "github",
        "linkedin", "adobe", "dhl", "fedex", "ups", "bkash", "nagad", "daraz", "pathao", "foodpanda"
    ];

    private static readonly string[] AllowedDomains =
    [
        "google.com", "gstatic.com", "googleapis.com", "youtube.com", "android.com",
        "microsoft.com", "microsoftonline.com", "live.com", "office.com", "office365.com",
        "windowsupdate.com", "windows.com", "msftncsi.com", "msftconnecttest.com",
        "apple.com", "icloud.com", "mozilla.org", "mozilla.net", "cloudflare.com",
        "github.com", "githubusercontent.com", "amazon.com", "amazonaws.com",
        "akamai.net", "akamaiedge.net", "fastly.net", "jsdelivr.net", "unpkg.com",
        "bing.com", "duckduckgo.com", "wikipedia.org", "stackoverflow.com"
    ];

    private static readonly string[] AbusedTlds =
    [
        "zip", "mov", "top", "xyz", "icu", "click", "country", "stream", "gq", "tk", "ml",
        "cf", "ga", "work", "cam", "rest", "buzz", "cyou", "sbs", "lol", "quest", "monster"
    ];

    private static readonly string[] DynamicDnsSuffixes =
    [
        "duckdns.org", "no-ip.org", "no-ip.biz", "hopto.org", "zapto.org", "ddns.net",
        "dynu.net", "ngrok.io", "ngrok-free.app", "trycloudflare.com", "workers.dev",
        "serveo.net", "localtunnel.me", "pagekite.me"
    ];

    private static readonly string[] SecondLevelSuffixes =
    [
        "co", "com", "net", "org", "gov", "ac", "edu", "or", "ne", "go", "mil"
    ];

    private static readonly string[] LoginWords =
    [
        "login", "signin", "sign-in", "verify", "secure", "account", "update", "billing", "password", "wallet"
    ];

    private static readonly string[] DangerousExtensions =
    [
        ".exe", ".scr", ".js", ".vbs", ".bat", ".cmd", ".msi", ".jar", ".hta", ".ps1", ".lnk", ".iso"
    ];

    /// <summary>Scores one address. Never throws; unparsable input is reported as exempt.</summary>
    public static UrlAssessment Assess(string? url, string? processPath = null)
    {
        var raw = (url ?? string.Empty).Trim();

        if (!TryParse(raw, out var uri))
            return Exempt(raw);

        var host = uri.IdnHost.ToLowerInvariant();
        if (host.Length == 0)
            return Exempt(raw);

        if (IsLocal(host))
            return new UrlAssessment(uri.ToString(), host, 0, ThreatSeverity.Low, IsExempt: true, []);

        if (IsAllowed(host))
            return new UrlAssessment(uri.ToString(), host, 0, ThreatSeverity.Low, IsExempt: true, []);

        var signals = new List<ThreatSignal>();
        void Add(string id)
        {
            var rule = Rules.FirstOrDefault(r => r.Id == id);
            if (rule is not null && signals.All(s => s.Id != id))
                signals.Add(new ThreatSignal(rule.Id, rule.Weight, rule.Title));
        }

        var displayHost = UnicodeHost(host);
        var registrable = RegistrableDomain(displayHost);
        var domainLabel = registrable.Split('.')[0];

        if (IPAddress.TryParse(host.Trim('[', ']'), out _))
            Add("ip-host");

        if (uri.UserInfo.Length > 0)
            Add("userinfo");

        if (host.Contains("xn--", StringComparison.OrdinalIgnoreCase))
            Add("idn");

        if (HasMixedScripts(displayHost))
            Add("mixed-script");

        var brand = ClosestBrand(domainLabel);
        if (brand is not null)
            Add("brand-lookalike");

        if (brand is null && displayHost != registrable)
        {
            var brandsInSubdomain = displayHost.Split('.')
                .SkipLast(2)
                .Any(label => Brands.Contains(label, StringComparer.OrdinalIgnoreCase));

            if (brandsInSubdomain)
                Add("brand-subdomain");
        }

        if (AbusedTlds.Contains(registrable.Split('.').Last(), StringComparer.OrdinalIgnoreCase))
            Add("bad-tld");

        if (DynamicDnsSuffixes.Any(suffix => host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)))
            Add("ddns");

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            var path = uri.AbsolutePath.ToLowerInvariant();
            if (LoginWords.Any(path.Contains))
                Add("http-login");
        }

        if (HasDisguisedExtension(uri.AbsolutePath))
            Add("double-extension");

        if (HasRandomLabel(host))
            Add("random-label");

        if (host.Split('.').Length >= 5)
            Add("deep-subdomain");

        if (uri.IsDefaultPort || (uri.Port != 80 && uri.Port != 443))
        {
            if (!uri.IsDefaultPort)
                Add("odd-port");
        }

        if (HasEncodedPath(uri))
            Add("encoded-path");

        if (!string.IsNullOrWhiteSpace(processPath) && RunsFromTemp(processPath))
            Add("temp-process");

        var score = Math.Min(100, signals.Sum(signal => signal.Weight));
        var severity = score >= CriticalScore ? ThreatSeverity.Critical
            : score >= SuspiciousScore ? ThreatSeverity.High
            : score >= MediumScore ? ThreatSeverity.Medium
            : ThreatSeverity.Low;

        return new UrlAssessment(uri.ToString(), host, score, severity, IsExempt: false, signals);
    }

    private static UrlAssessment Exempt(string url) =>
        new(url, string.Empty, 0, ThreatSeverity.Low, IsExempt: true, []);

    private static bool TryParse(string url, out Uri uri)
    {
        uri = null!;
        if (url.Length == 0 || url.Length > 2048)
            return false;

        // Address bars omit the scheme for plain http sites.
        var candidate = url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
            return false;

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;

        if (!parsed.Host.Contains('.') && !IPAddress.TryParse(parsed.Host, out _))
            return false;

        uri = parsed;
        return true;
    }

    private static bool IsLocal(string host)
    {
        if (host is "localhost" || host.EndsWith(".local") || host.EndsWith(".lan")
            || host.EndsWith(".internal") || host.EndsWith(".home.arpa"))
            return true;

        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address))
            return false;

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || IPAddress.IsLoopback(address))
            return true;

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4
               && (bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254));
    }

    private static bool IsAllowed(string host) =>
        AllowedDomains.Any(allowed =>
            host.Equals(allowed, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase));

    /// <summary>Approximate eTLD+1 without shipping a public-suffix list.</summary>
    private static string RegistrableDomain(string host)
    {
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length <= 2)
            return host;

        var secondLast = labels[^2];
        var last = labels[^1];
        var isTwoPartSuffix = last.Length == 2 && SecondLevelSuffixes.Contains(secondLast, StringComparer.OrdinalIgnoreCase);

        return string.Join('.', labels.TakeLast(isTwoPartSuffix ? 3 : 2));
    }

    private static string? ClosestBrand(string label)
    {
        if (label.Length < 5 || Brands.Contains(label, StringComparer.OrdinalIgnoreCase))
            return null;

        foreach (var brand in Brands)
        {
            if (Math.Abs(brand.Length - label.Length) > 2)
                continue;

            if (Levenshtein.Distance(label, brand, 3) is > 0 and <= 2)
                return brand;
        }

        return null;
    }

    /// <summary>Decodes punycode labels so lookalike checks see the real characters.</summary>
    private static string UnicodeHost(string host)
    {
        if (!host.Contains("xn--", StringComparison.OrdinalIgnoreCase))
            return host;

        var mapping = new IdnMapping();
        var labels = host.Split('.');
        for (var i = 0; i < labels.Length; i++)
        {
            if (!labels[i].StartsWith("xn--", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                labels[i] = mapping.GetUnicode(labels[i]);
            }
            catch (ArgumentException)
            {
                // Leave the raw label: a malformed punycode name is already suspicious.
            }
        }

        return string.Join('.', labels);
    }

    private static bool HasMixedScripts(string host)
    {
        var latin = host.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
        var other = host.Any(c => c is >= '\u0400' and <= '\u04FF'
                                  or >= '\u0370' and <= '\u03FF'
                                  or >= '\u0590' and <= '\u05FF'
                                  or >= '\u0600' and <= '\u06FF');

        return latin && other;
    }

    private static bool HasDisguisedExtension(string path)
    {
        var name = path.Split('/').LastOrDefault() ?? string.Empty;
        if (name.Length < 8)
            return false;

        var lowered = name.ToLowerInvariant();
        var lastDot = lowered.LastIndexOf('.');
        var earlier = lowered.LastIndexOf('.', Math.Max(0, lastDot - 1));

        return lastDot > 0
               && earlier > 0
               && DangerousExtensions.Contains(lowered[lastDot..])
               && lowered[(earlier + 1)..lastDot].Length is >= 2 and <= 5;
    }

    private static bool HasRandomLabel(string host)
    {
        foreach (var label in host.Split('.').SkipLast(1))
        {
            if (label.Length is < 12 or > 63)
                continue;

            if (label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase))
                continue;

            var digits = label.Count(char.IsAsciiDigit);
            if (digits >= 3 && (double)digits / label.Length >= 0.3)
                return true;

            if (ShannonEntropy(label) >= 3.5)
                return true;
        }

        return false;
    }

    private static double ShannonEntropy(string value)
    {
        if (value.Length == 0)
            return 0;

        var counts = new Dictionary<char, int>();
        foreach (var c in value)
            counts[c] = counts.GetValueOrDefault(c) + 1;

        var entropy = 0.0;
        foreach (var count in counts.Values)
        {
            var p = (double)count / value.Length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    private static bool HasEncodedPath(Uri uri)
    {
        if (uri.AbsolutePath.Length < 60)
            return false;

        var blob = 0;
        foreach (var c in uri.AbsolutePath)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '-' or '_')
                blob++;
            else
                blob = 0;

            if (blob >= 32)
                return true;
        }

        return false;
    }

    private static bool RunsFromTemp(string path) =>
        path.Contains(@"\temp\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\downloads\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\appdata\local\temp", StringComparison.OrdinalIgnoreCase);

    private static class Levenshtein
    {
        public static int Distance(string a, string b, int limit)
        {
            if (a.Length == 0)
                return b.Length;
            if (b.Length == 0)
                return a.Length;

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];

            for (var j = 0; j <= b.Length; j++)
                previous[j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                var rowMin = current[0];

                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(
                        Math.Min(current[j - 1] + 1, previous[j] + 1),
                        previous[j - 1] + cost);
                    rowMin = Math.Min(rowMin, current[j]);
                }

                if (rowMin > limit)
                    return limit + 1;

                (previous, current) = (current, previous);
            }

            return previous[b.Length];
        }
    }
}
