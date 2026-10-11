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
}
