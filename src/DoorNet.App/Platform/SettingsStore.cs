using System.IO;
using System.Text.Json;

namespace DoorNet.App.Platform;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";

    /// <summary>
    /// Relaunch through UAC on startup. Per-app byte usage (kernel ETW) and firewall rules
    /// need administrator rights; set to false to always start unprivileged.
    /// </summary>
    public bool AutoElevate { get; set; } = true;

    /// <summary>Hosts the user marked as safe; the URL analyzer leaves them alone.</summary>
    public List<string> WhitelistedHosts { get; set; } = [];

    /// <summary>VirusTotal key entered in the app; empty falls back to .env / environment.</summary>
    public string VirusTotalApiKey { get; set; } = string.Empty;
}

/// <summary>Small JSON store for UI preferences under %LocalAppData%\DoorNet.</summary>
public static class SettingsStore
{
    private static readonly string FolderPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoorNet");

    private static readonly string FilePath = Path.Combine(FolderPath, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Adds a host to the whitelist, keeping the rest of the settings untouched.</summary>
    public static void WhitelistHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return;

        var settings = Load();
        if (settings.WhitelistedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            return;

        settings.WhitelistedHosts.Add(host);
        Save(settings);
    }

    /// <summary>Stores (or clears, when empty) the VirusTotal key entered in the app.</summary>
    public static void SaveApiKey(string? key)
    {
        var settings = Load();
        settings.VirusTotalApiKey = key?.Trim() ?? string.Empty;
        Save(settings);
    }

    /// <summary>Removes one host from the whitelist, keeping the rest of the settings.</summary>
    public static void RemoveWhitelistedHost(string host)
    {
        var settings = Load();
        var removed = settings.WhitelistedHosts.RemoveAll(value =>
            string.Equals(value, host, StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
            Save(settings);
    }
}
