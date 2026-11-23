using System.Text;

namespace DoorNet.Core.Scanning;

/// <summary>Formatting and validation helpers for hardware addresses.</summary>
public static class MacAddress
{
    /// <summary>Formats the first six bytes as <c>AA:BB:CC:DD:EE:FF</c>.</summary>
    public static string Format(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6)
            return string.Empty;

        var builder = new StringBuilder(17);
        for (var i = 0; i < 6; i++)
        {
            if (i > 0)
                builder.Append(':');

            builder.Append(bytes[i].ToString("X2"));
        }

        return builder.ToString();
    }

    /// <summary>Strips separators and upper-cases the hex digits.</summary>
    public static string Normalize(string mac)
    {
        var builder = new StringBuilder(12);
        foreach (var c in mac)
        {
            if (Uri.IsHexDigit(c))
                builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>The first six hex digits, the vendor prefix, or an empty string.</summary>
    public static string Prefix(string mac)
    {
        var normalized = Normalize(mac);
        return normalized.Length >= 6 ? normalized[..6] : string.Empty;
    }

    /// <summary>
    /// Locally administered (randomised) addresses are not in the IEEE registry, so a vendor
    /// lookup would always miss. Bit 1 of the first octet marks them.
    /// </summary>
    public static bool IsLocallyAdministered(string mac)
    {
        var normalized = Normalize(mac);
        if (normalized.Length < 2)
            return false;

        var first = Convert.ToByte(normalized[..2], 16);
        return (first & 0x02) != 0;
    }

    /// <summary>True for a real unicast address (not broadcast, multicast or all zero).</summary>
    public static bool IsUnicast(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6)
            return false;

        if (bytes[..6].IndexOfAnyExcept((byte)0) < 0)
            return false;

        // Bit 0 of the first octet marks multicast/broadcast addresses.
        return (bytes[0] & 0x01) == 0;
    }
}
