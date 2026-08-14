using VegaBridgeApp.Models.Closures;
using VegaBridgeApp.Models.Valhalla;

namespace VegaBridgeApp.Services.Closures;

/// <summary>
/// Checks a route for road closures / restrictions using the Overpass API
/// (live OpenStreetMap data). Intended as a pre-route or en-route safety
/// check: the caller provides the route geometry, the service queries a
/// corridor around it and returns closures sorted by distance.
/// </summary>
public interface IRoadClosureService
{
    /// <summary>
    /// Queries Overpass for closures near the given route.
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
        double corridorMeters = 200,
        CancellationToken cancellationToken = default);
}
