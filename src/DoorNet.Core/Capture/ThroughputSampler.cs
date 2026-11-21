using DoorNet.Core.Models;

namespace DoorNet.Core.Capture;

/// <summary>
/// Converts monotonic adapter counters into smoothed rates and session totals.
/// Stateful: create one instance per monitoring session and feed it every tick.
/// </summary>
public sealed class ThroughputSampler
{
    private readonly double _smoothing;
    private AdapterCounters _previous;
    private DateTimeOffset _previousAt;
    private bool _hasBaseline;
    private double _downloadRate;
    private double _uploadRate;

    /// <param name="smoothing">
    /// EMA factor in (0, 1]. Lower is smoother but lags bursts; 0.35 tracks roughly 3 seconds.
    /// </param>
    public ThroughputSampler(double smoothing = 0.35)
    {
        if (smoothing is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(smoothing), "Smoothing must be in (0, 1].");

        _smoothing = smoothing;
    }

    public ulong SessionBytesReceived { get; private set; }
    public ulong SessionBytesSent { get; private set; }

    public ThroughputReading Sample(AdapterCounters current, DateTimeOffset now)
    {
        if (!_hasBaseline)
        {
            _previous = current;
            _previousAt = now;
            _hasBaseline = true;
            return new ThroughputReading(0, 0, SessionBytesReceived, SessionBytesSent, current.AdapterSummary);
        }

        var elapsed = (now - _previousAt).TotalSeconds;
        if (elapsed <= 0)
            return Current(current.AdapterSummary);

        var receivedDelta = Delta(current.BytesReceived, _previous.BytesReceived);
        var sentDelta = Delta(current.BytesSent, _previous.BytesSent);

        // Adapter reset (sleep/resume, driver reload) or a counter rollback: re-baseline
        // without reporting a bogus spike.
        if (current.BytesReceived < _previous.BytesReceived || current.BytesSent < _previous.BytesSent)
        {
            _previous = current;
            _previousAt = now;
            return new ThroughputReading(0, 0, SessionBytesReceived, SessionBytesSent, current.AdapterSummary);
        }

        SessionBytesReceived += receivedDelta;
        SessionBytesSent += sentDelta;

        var instantDown = receivedDelta / elapsed;
        var instantUp = sentDelta / elapsed;

        _downloadRate = Blend(_downloadRate, instantDown);
        _uploadRate = Blend(_uploadRate, instantUp);

        _previous = current;
        _previousAt = now;

        return Current(current.AdapterSummary);
    }

    /// <summary>Clears session totals and rates; the next sample re-establishes a baseline.</summary>
    public void Reset()
    {
        SessionBytesReceived = 0;
        SessionBytesSent = 0;
        _downloadRate = 0;
        _uploadRate = 0;
        _hasBaseline = false;
    }

    private ThroughputReading Current(string adapterSummary) =>
        new(_downloadRate, _uploadRate, SessionBytesReceived, SessionBytesSent, adapterSummary);

    private double Blend(double previous, double instant) =>
        _hasBaseline ? previous + (_smoothing * (instant - previous)) : instant;

    private static ulong Delta(ulong current, ulong previous) =>
        current >= previous ? current - previous : 0;
}
