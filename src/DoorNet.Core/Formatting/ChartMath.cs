namespace DoorNet.Core.Formatting;

/// <summary>Axis maths for the throughput chart.</summary>
public static class ChartMath
{
    /// <summary>
    /// Rounds a peak value up to a readable axis maximum (1/2/2.5/5 × 10^n) so gridline
    /// labels land on round byte rates instead of 3.87 MB/s.
    /// </summary>
    public static double NiceMax(double peak, double floor = 4096)
    {
        if (double.IsNaN(peak) || peak <= floor)
            return floor;

        var exponent = Math.Floor(Math.Log10(peak));
        var magnitude = Math.Pow(10, exponent);
        var normalized = peak / magnitude;

        var step = normalized switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 2.5 => 2.5,
            <= 5 => 5,
            _ => 10
        };

        return step * magnitude;
    }
}
