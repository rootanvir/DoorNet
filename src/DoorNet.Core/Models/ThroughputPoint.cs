namespace DoorNet.Core.Models;

/// <summary>One sampled interval of aggregate network traffic.</summary>
public readonly record struct ThroughputPoint(
    DateTimeOffset Timestamp,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond);
