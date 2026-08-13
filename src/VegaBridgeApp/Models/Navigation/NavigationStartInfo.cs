namespace VegaBridgeApp.Models.Navigation;

/// <summary>
/// Information about the start of a navigation.
/// </summary>
public sealed class NavigationStartInfo
{
    public required double TotalDistanceKm { get; init; }
    public required double TotalTimeMin { get; init; }
    public required int ManeuverCount { get; init; }
}
