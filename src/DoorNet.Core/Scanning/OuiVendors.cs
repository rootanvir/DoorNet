namespace DoorNet.Core.Scanning;

/// <summary>
/// A small, curated vendor table for the OUI (first three bytes) of common home and office
/// hardware. It is deliberately not the full IEEE registry - unknown prefixes fall back to
/// "Unknown vendor" instead of guessing.
/// </summary>
public static class OuiVendors
{
    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        // Virtual machines and hypervisors
        ["000C29"] = "VMware",
        ["005056"] = "VMware",
        ["080027"] = "Oracle VirtualBox",
        ["00155D"] = "Microsoft (Hyper-V)",
        ["000D3A"] = "Microsoft",

        // Apple
        ["0017F2"] = "Apple",
        ["0026BB"] = "Apple",
        ["3C0754"] = "Apple",
        ["8C8590"] = "Apple",
        ["A4C361"] = "Apple",
        ["F0DBF8"] = "Apple",
        ["28CFE9"] = "Apple",
        ["442A60"] = "Apple",
        ["D89695"] = "Apple",
        ["F01898"] = "Apple",

        // Google
        ["001A11"] = "Google",
        ["3C5AB4"] = "Google",
        ["F4F5D8"] = "Google",
        ["18B430"] = "Google Nest",

        // Samsung
        ["3C5A37"] = "Samsung",
        ["8C7712"] = "Samsung",
        ["34AA8B"] = "Samsung",

        // Phones and tablets
        ["640980"] = "Xiaomi",
        ["7811DC"] = "Xiaomi",
        ["F0B429"] = "Xiaomi",
        ["508F4C"] = "Xiaomi",
        ["8CBEBE"] = "Xiaomi",
        ["34CE00"] = "Xiaomi",
        ["9C99A0"] = "Xiaomi",

        // Network gear
        ["68D79A"] = "Ubiquiti",
        ["245A4C"] = "Ubiquiti",
        ["788A20"] = "Ubiquiti",
        ["50C7BF"] = "TP-Link",
        ["60A4D0"] = "TP-Link",
        ["0024B2"] = "Netgear",
        ["20E52A"] = "Netgear",
        ["A040A0"] = "Netgear",
        ["2C56DC"] = "ASUS",
        ["1C872C"] = "ASUS",
        ["049226"] = "ASUS",
        ["0022B0"] = "D-Link",
        ["001E58"] = "D-Link",
        ["001B11"] = "D-Link",
        ["0018E7"] = "Cisco",
        ["001B54"] = "Cisco",
        ["00000C"] = "Cisco",
        ["001DD6"] = "ARRIS",

        // Single-board computers and embedded
        ["B827EB"] = "Raspberry Pi",
        ["DCA632"] = "Raspberry Pi",
        ["E45F01"] = "Raspberry Pi",
        ["240AC4"] = "Espressif",
        ["30AEA4"] = "Espressif",
        ["3C71BF"] = "Espressif",
        ["84CCA8"] = "Espressif",
        ["A4CF12"] = "Espressif",
        ["DC4F22"] = "Espressif",
        ["5CCF7F"] = "Espressif",

        // Media and entertainment
        ["B0A737"] = "Roku",
        ["CC6DA0"] = "Roku",
        ["D83134"] = "Roku",
        ["AC3A7A"] = "Roku",
        ["000E58"] = "Sonos",
        ["48A6B8"] = "Sonos",
        ["5CAAFD"] = "Sonos",
        ["949F3E"] = "Sonos",
        ["001788"] = "Philips Hue",
        ["98B6E9"] = "Nintendo",
        ["58BDA3"] = "Nintendo",
        ["78A2A0"] = "Nintendo",

        // PC and network chipsets
        ["00A0C9"] = "Intel",
        ["001B21"] = "Intel",
        ["3C7C3F"] = "Intel",
        ["F4CE46"] = "Intel",
        ["001C42"] = "Parallels",
        ["525400"] = "QEMU"
    };

    /// <summary>Vendor name for a MAC address, or <c>null</c> when the prefix is unknown.</summary>
    public static string? Lookup(string mac) =>
        Vendors.TryGetValue(MacAddress.Prefix(mac), out var vendor) ? vendor : null;

    /// <summary>Number of known prefixes, handy for tests and diagnostics.</summary>
    public static int Count => Vendors.Count;
}
