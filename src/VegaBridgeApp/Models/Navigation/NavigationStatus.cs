namespace VegaBridgeApp.Models.Navigation;

public class NavigationStatus
{
    public double SpeedKmh { get; init; }
    public double DistanceToNextTurnM { get; init; }
    public double RemainingDistanceKm { get; init; }
    public double RemainingTimeMin { get; init; }
    public int CurrentManeuverIndex { get; init; }
    public int DisplayManeuverIndex { get; init; }
    public int TotalManeuvers { get; init; }
    public double Heading { get; init; }
    public double Accuracy { get; init; }
    public bool IsStationary { get; init; }

    /// <summary>
    /// No GPS (tunnel): position estimated along the route from the last
    /// speed. Speed and accuracy are those of the last fix.
    /// </summary>
    public bool IsEstimated { get; init; }
}
