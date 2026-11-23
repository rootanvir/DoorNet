using System.Net;
using DoorNet.Core.Scanning;
using Xunit;

namespace DoorNet.Core.Tests;

public sealed class ScannerTests
{
    [Fact]
    public void MacAddress_FormatsSixBytes()
    {
        var bytes = new byte[] { 0xA4, 0xC3, 0x61, 0x0B, 0x1C, 0x2D };
        Assert.Equal("A4:C3:61:0B:1C:2D", MacAddress.Format(bytes));
    }

    [Fact]
    public void MacAddress_NormalizesSeparators()
    {
        Assert.Equal("A4C3610B1C2D", MacAddress.Normalize("a4-c3-61-0b-1c-2d"));
        Assert.Equal("A4C361", MacAddress.Prefix("A4:C3:61:0B:1C:2D"));
    }

    [Fact]
    public void MacAddress_RejectsMulticastAndEmpty()
    {
        Assert.False(MacAddress.IsUnicast(new byte[] { 0x01, 0x00, 0x5E, 0x00, 0x00, 0x01 }));
        Assert.False(MacAddress.IsUnicast(new byte[] { 0, 0, 0, 0, 0, 0 }));
        Assert.True(MacAddress.IsUnicast(new byte[] { 0xA4, 0xC3, 0x61, 0x0B, 0x1C, 0x2D }));
    }

    [Theory]
    [InlineData("A4:C3:61:0B:1C:2D", "Apple")]
    [InlineData("b8-27-eb-01-02-03", "Raspberry Pi")]
    [InlineData("24:0A:C4:00:11:22", "Espressif")]
    [InlineData("50:C7:BF:AA:BB:CC", "TP-Link")]
    public void OuiVendors_KnownPrefixes(string mac, string vendor) =>
        Assert.Equal(vendor, OuiVendors.Lookup(mac));

    [Fact]
    public void OuiVendors_UnknownPrefixIsNull() =>
        Assert.Null(OuiVendors.Lookup("02:00:00:00:00:01"));

    [Fact]
    public void LanSubnet_ComputesCidrAndHostCount()
    {
        var address = IPAddress.Parse("192.168.1.42");
        var mask = IPAddress.Parse("255.255.255.0");

        Assert.Equal("192.168.1.0/24", LanSubnet.Cidr(address, mask));
        Assert.Equal(24, LanSubnet.PrefixLength(0xFFFFFF00));

        var hosts = LanSubnet.Hosts(address, mask);
        Assert.Equal(253, hosts.Count);
        Assert.DoesNotContain(address, hosts);
        Assert.Contains(IPAddress.Parse("192.168.1.1"), hosts);
        Assert.Contains(IPAddress.Parse("192.168.1.254"), hosts);
    }

    [Fact]
    public void LanSubnet_NarrowsLargeSubnetToTwentyFour()
    {
        var hosts = LanSubnet.Hosts(IPAddress.Parse("10.0.5.7"), IPAddress.Parse("255.255.0.0"));
        Assert.Equal(253, hosts.Count);
        Assert.All(hosts, host => Assert.StartsWith("10.0.5.", host.ToString()));
    }

    [Fact]
    public void DeviceClassifier_GatewayIsRouter()
    {
        var (kind, system) = DeviceClassifier.Classify(null, "home", [], isGateway: true, isThisDevice: false);
        Assert.Equal(DeviceKind.Router, kind);
        Assert.Equal("Router firmware", system);
    }

    [Fact]
    public void DeviceClassifier_WindowsPortsMeanComputer()
    {
        var (kind, system) = DeviceClassifier.Classify("Dell", "desk", [135, 445], false, false);
        Assert.Equal(DeviceKind.Computer, kind);
        Assert.Equal("Windows", system);
    }

    [Fact]
    public void DeviceClassifier_PrinterPortWins()
    {
        var (kind, _) = DeviceClassifier.Classify("HP", "office", [9100], false, false);
        Assert.Equal(DeviceKind.Printer, kind);
    }

    [Fact]
    public void DeviceClassifier_TvByCastPort()
    {
        var (kind, system) = DeviceClassifier.Classify("Roku", "living", [8009], false, false);
        Assert.Equal(DeviceKind.Tv, kind);
        Assert.Equal("Smart TV", system);
    }

    [Fact]
    public void DeviceClassifier_iPhonePortMeansPhone()
    {
        var (kind, system) = DeviceClassifier.Classify("Apple", "iPhone", [62078], false, false);
        Assert.Equal(DeviceKind.Phone, kind);
        Assert.Equal("iOS", system);
    }

    [Fact]
    public void DeviceClassifier_EspressifIsIot()
    {
        var (kind, _) = DeviceClassifier.Classify("Espressif", "plug", [], false, false);
        Assert.Equal(DeviceKind.InternetOfThings, kind);
    }

    [Fact]
    public void DeviceClassifier_SshMeansUnix()
    {
        var (kind, system) = DeviceClassifier.Classify("Raspberry Pi", "pi", [22], false, false);
        Assert.Equal(DeviceKind.Computer, kind);
        Assert.Equal("Linux / Unix", system);
    }

    [Fact]
    public void DeviceClassifier_UnknownWhenNothingMatches()
    {
        var (kind, system) = DeviceClassifier.Classify("", "", [], false, false);
        Assert.Equal(DeviceKind.Unknown, kind);
        Assert.Equal("—", system);
    }
}
