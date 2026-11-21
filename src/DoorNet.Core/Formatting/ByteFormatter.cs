using System.Globalization;

namespace DoorNet.Core.Formatting;

/// <summary>Byte and rate formatting with tabular, non-jittering output.</summary>
public static class ByteFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>e.g. "1.24 MB/s", "0 B/s".</summary>
    public static string FormatRate(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond <= 0)
            return "0 B/s";

        var (value, unit) = Scale(bytesPerSecond);
        return $"{FormatNumber(value, unit)} {unit}/s";
    }

    /// <summary>Splits a rate for large/tile display: "1.24" + "MB/s".</summary>
    public static (string Value, string Unit) SplitRate(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond <= 0)
            return ("0", "B/s");

        var (value, unit) = Scale(bytesPerSecond);
        return (FormatNumber(value, unit), $"{unit}/s");
    }

    /// <summary>e.g. "4.7 GB", "812 MB".</summary>
    public static string FormatBytes(double bytes)
    {
        if (double.IsNaN(bytes) || bytes <= 0)
            return "0 B";

        var (value, unit) = Scale(bytes);
        return $"{FormatNumber(value, unit)} {unit}";
    }

    public static (string Value, string Unit) SplitBytes(double bytes)
    {
        if (double.IsNaN(bytes) || bytes <= 0)
            return ("0", "B");

        var (value, unit) = Scale(bytes);
        return (FormatNumber(value, unit), unit);
    }

    /// <summary>1234 → "1,234".</summary>
    public static string FormatCount(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static (double Value, string Unit) Scale(double bytes)
    {
        var unitIndex = 0;
        var value = bytes;
        while (value >= 1024 && unitIndex < Units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return (value, Units[unitIndex]);
    }

    private static string FormatNumber(double value, string unit)
    {
        // Below one unit, integers read better ("0 B/s"); above it, fixed decimals keep
        // the field width stable while the value moves.
        if (unit == "B")
            return value.ToString("0", CultureInfo.InvariantCulture);

        var decimals = value < 10 ? 2 : value < 100 ? 1 : 0;
        return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }
}
