using System.Net;
using System.Net.Sockets;

namespace DoorNet.App.Platform;

/// <summary>Latitude/longitude and carrier details resolved for a public IP address.</summary>
public readonly record struct GeoPoint(
    double Latitude,
    double Longitude,
    string? City,
    string? Country,
    string? Isp = null,
    string? Org = null,
    string? Asn = null,
    string? Address = null)
{
    public string LocationText => string.Join(
        ", ",
        new[] { City, Country }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public string NetworkText => string.IsNullOrWhiteSpace(Isp) ? Org ?? string.Empty : Isp;
}

/// <summary>A dot to place on the world map.</summary>
public readonly record struct GeoMarker(
    double Latitude,
    double Longitude,
    bool IsLocal,
    string Label,
    string? City = null,
    string? Country = null);
