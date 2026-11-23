using System.Net;
using System.Net.Sockets;

namespace DoorNet.App.Platform;

/// <summary>Latitude/longitude resolved for a public IP address.</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude, string? City, string? Country);

/// <summary>A dot to place on the world map.</summary>
public readonly record struct GeoMarker(
    double Latitude,
    double Longitude,
    bool IsLocal,
    string Label,
    string? City = null,
    string? Country = null);
