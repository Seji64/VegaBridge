using VegaBridgeApp.Models.Closures;
using VegaBridgeApp.Models.Valhalla;

namespace VegaBridgeApp.Services.Closures;

/// <summary>
/// Aggregates the user-selected road-closure providers (Overpass/OSM,
/// MobiData BW, …) along a route. Intended as a pre-route or en-route
/// safety check: the caller provides the route geometry, the active
/// providers query a corridor around it, and the results are merged
/// (deduplicated, nearest first).
/// </summary>
public interface IRoadClosureService
{
    /// <summary>
    /// Queries all enabled providers for closures near the given route.
    /// </summary>
    /// <param name="routeCoords">Route geometry (at least 2 points).</param>
    /// <param name="corridorMeters">Half-width of the corridor around the route to scan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A result with closures (nearest first). Never throws – failures are
    /// reported via <see cref="RoadClosureCheckResult.IsSuccess"/>.
    /// </returns>
    Task<RoadClosureCheckResult> CheckRouteAsync(
        IReadOnlyList<Coordinate> routeCoords,
        double corridorMeters = RoadClosureService.DefaultCorridorMeters,
        CancellationToken cancellationToken = default);
}
