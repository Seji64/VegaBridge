namespace VegaBridgeApp.Utils;

/// <summary>
/// Rounding of values shown to the rider – exact numbers only flicker and
/// are hard to read at a glance.
/// </summary>
public static class DisplayRounding
{
    /// <summary>
    /// Distance to the next turn like navigation apps show it: 10 m steps
    /// below 100 m, 50 m steps below 1 km, above in km with one decimal.
    /// </summary>
    public static (double Value, bool IsKm) TurnDistance(double meters)
    {
        double rounded = meters < 100
            ? Math.Round(Math.Max(meters, 0) / 10, MidpointRounding.AwayFromZero) * 10
            : Math.Round(meters / 50, MidpointRounding.AwayFromZero) * 50;
        return rounded < 1000 ? (rounded, false) : (Math.Round(meters / 1000, 1, MidpointRounding.AwayFromZero), true);
    }

    /// <summary>Duration rounded to whole minutes, split into hours and minutes.</summary>
    public static (int Hours, int Minutes) Duration(double minutes)
    {
        int total = (int)Math.Round(Math.Max(minutes, 0), MidpointRounding.AwayFromZero);
        return (total / 60, total % 60);
    }
}
