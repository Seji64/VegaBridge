using VegaBridgeApp.Models.Valhalla;

namespace VegaBridgeApp.Models.Routes;

/// <summary>
/// Result of parsing a GPX file – contains extracted track and/or route data.
/// </summary>
public class GpxParseResult
{
    public List<Coordinate>? TrackPoints { get; init; }
    public string TrackName { get; init; } = "";
    public int TrackPointCount => TrackPoints?.Count ?? 0;
    public bool HasTrack => TrackPoints?.Count >= 2;

    public List<Coordinate>? RoutePoints { get; init; }
    public string RouteName { get; init; } = "";
    public int RoutePointCount => RoutePoints?.Count ?? 0;
    public bool HasRoute => RoutePoints?.Count >= 2;

    public bool IsValid => HasTrack || HasRoute;
    public bool IsAmbiguous => HasTrack && HasRoute;
}
