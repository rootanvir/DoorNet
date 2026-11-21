using System.Net;
using DoorNet.Core.Networking;

namespace DoorNet.Core.Tests;

public class AddressScopeTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.20.30")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void LocalOnlyAddresses_AreIgnored(string address)
    {
        Assert.True(AddressScope.IsLocalOnly(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.5")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void RoutableAddresses_AreCounted(string address)
    {
        Assert.False(AddressScope.IsLocalOnly(IPAddress.Parse(address)));
    }
}
