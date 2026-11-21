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
}
