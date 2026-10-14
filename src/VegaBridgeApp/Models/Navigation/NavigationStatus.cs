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
    /// <summary>Intermediate waypoints not reached yet (the destination excluded).</summary>
    public int RemainingWaypoints { get; init; }
    public double Heading { get; init; }
    public double Accuracy { get; init; }
    public bool IsStationary { get; init; }
    public bool IsEstimated { get; init; } // no GPS: position estimated along the route
    /// <summary>
    /// Driving side at the displayed maneuver (Valhalla edge.drive_on_right):
    /// true right-hand, false left-hand traffic, null while unknown.
    /// </summary>
    public bool? DriveOnRight { get; init; }
}
