namespace DoorNet.Core.Formatting;

/// <summary>
/// Scales a rate into a 0..1 bar length. Per-app rates span B/s to MB/s, so linear scaling
/// makes everything except the busiest row invisible; a square root keeps small flows
/// visible without letting one bulk transfer flatten the rest.
/// </summary>
public static class MeterMath
{
    public const double MinimumVisible = 0.04;

    public static double Scale(double rate, double max, double minimumVisible = MinimumVisible)
    {
        if (double.IsNaN(rate) || double.IsNaN(max) || rate <= 0 || max <= 0)
            return 0;

        var ratio = Math.Clamp(rate / max, 0, 1);
        return Math.Max(minimumVisible, Math.Sqrt(ratio));
    }
}
