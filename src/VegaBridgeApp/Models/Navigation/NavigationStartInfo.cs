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
    /// Start coordinates of the route (first route point), used by the BLE
    /// plugin for the DEST frame. Null when the route has no geometry.
    /// </summary>
    public double? StartLatitude { get; init; }
    public double? StartLongitude { get; init; }
}
