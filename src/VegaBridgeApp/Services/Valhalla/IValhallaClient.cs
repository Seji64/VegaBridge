using VegaBridgeApp.Models.Valhalla;

namespace VegaBridgeApp.Services.Valhalla;

/// <summary>
/// Service interface for routing requests to a Valhalla server.
/// </summary>
public interface IValhallaClient
{
    /// <summary>
    /// Request a route (turn-by-turn directions) from the Valhalla server.
    /// </summary>
    /// <param name="request">The route request with locations and costing.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A ValhallaResult wrapping success or failure details.</returns>
    Task<Result> GetRouteAsync(RouteRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Map match raw GPS points to the Valhalla road network.
    /// Returns the best matched position for each input point (confidence-weighted).
    /// </summary>
    /// <param name="rawPoints">Raw GPS coordinates to match.</param>
    /// <param name="profile">Travel profile (e.g., "auto", "pedestrian").</param>
    /// <param name="radiusMeters">Search radius for matching.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A MapMatchResult with snapped points and confidence values.</returns>
    Task<MapMatchResult> MapMatchAsync(
        List<Coordinate> rawPoints,
        string profile = "auto",
        double radiusMeters = 50,
        CancellationToken cancellationToken = default);
}
