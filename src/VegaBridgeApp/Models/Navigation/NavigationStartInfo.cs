namespace VegaBridgeApp.Models.Navigation;

/// <summary>
/// Information about the start of a navigation.
/// </summary>
public sealed class NavigationStartInfo
{
    public required double TotalDistanceKm { get; init; }
    public required double TotalTimeMin { get; init; }
    public required int ManeuverCount { get; init; }

    /// <summary>
    /// Final destination of the route, used by the BLE plugin for the DEST
    /// frame (official MV Ride capture: DEST carries the destination, constant
    /// across reroutes).
    /// </summary>
    public double? DestinationLatitude { get; init; }
    public double? DestinationLongitude { get; init; }
}
