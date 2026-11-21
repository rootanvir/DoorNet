using System.Security.Cryptography;
using System.Text;

namespace DoorNet.Core.Firewall;

/// <summary>
/// Deterministic names for the firewall rules DoorNet owns. The path hash keeps rules for
/// two executables with the same file name apart, and lets us find/remove our own rules
/// without enumerating (and without colliding with rules the user created).
/// </summary>
public static class FirewallRuleNames
{
    public const string Group = "DoorNet";

    public static string ForPath(string executablePath)
    {
        var trimmed = executablePath.Trim();
        var normalized = trimmed.ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var suffix = Convert.ToHexString(hash)[..8];

        var fileName = Path.GetFileName(trimmed).ToLowerInvariant();
        if (fileName.Length > 60)
            fileName = fileName[..60];

        return $"{Group} block {fileName} #{suffix}";
    }
}
