using System.IO;

namespace DoorNet.App.Platform;

/// <summary>
/// Reads a key from the environment or from a .env file next to (or above) the executable.
/// The repo root .env is found by walking up from the build output folder, so development
/// builds and published builds both work without copying secrets around.
/// </summary>
public static class DotEnv
{
    private const int MaxParents = 8;

    public static string? Get(string name)
    {
        // A real environment variable always wins: easier to rotate without touching files.
        var fromEnvironment = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return fromEnvironment.Trim();

        foreach (var file in Candidates())
        {
            if (TryRead(file, name, out var value))
                return value;
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (var depth = 0; depth < MaxParents && directory is not null; depth++)
        {
            yield return Path.Combine(directory.FullName, ".env");
            directory = directory.Parent;
        }

        var current = Directory.GetCurrentDirectory();
        yield return Path.Combine(current, ".env");
    }

    private static bool TryRead(string path, string name, out string value)
    {
        value = string.Empty;

        try
        {
            if (!File.Exists(path))
                return false;

            foreach (var line in File.ReadAllLines(path))
            {
                var text = line.Trim();
                if (text.Length == 0 || text.StartsWith('#'))
                    continue;

                var separator = text.IndexOf('=');
                if (separator <= 0)
                    continue;

                if (!text[..separator].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                var raw = text[(separator + 1)..].Trim();
                if (raw.Length >= 2 && ((raw[0] == '"' && raw[^1] == '"') || (raw[0] == '\'' && raw[^1] == '\'')))
                    raw = raw[1..^1];

                if (raw.Length == 0)
                    return false;

                value = raw;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A broken or locked .env simply means "no key".
        }

        return false;
    }
}
