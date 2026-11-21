namespace DoorNet.Core.Models;

/// <summary>Result of one sampler tick.</summary>
public readonly record struct ThroughputReading(
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    ulong SessionBytesReceived,
    ulong SessionBytesSent,
    string AdapterSummary)
{
    public static ThroughputReading Empty { get; } = new(0, 0, 0, 0, "No active adapter");
}
