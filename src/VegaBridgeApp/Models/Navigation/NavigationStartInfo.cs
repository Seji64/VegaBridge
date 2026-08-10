namespace VegaBridgeApp.Models.Navigation;

/// <summary>
/// Informationen über den Start einer Navigation.
/// </summary>
public sealed class NavigationStartInfo
{
    public required double TotalDistanceKm { get; init; }
    public required double TotalTimeMin { get; init; }
    public required int ManeuverCount { get; init; }
}
