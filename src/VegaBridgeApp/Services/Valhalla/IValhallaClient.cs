using VegaBridgeApp.Models.Valhalla;

namespace VegaBridgeApp.Services.Valhalla;

/// <summary>
/// Service interface for routing requests to a Valhalla server.
/// </summary>
public interface IValhallaClient
{
    /// <summary>
    /// Request a route (turn‑by‑turn directions) from a Valhalla server.
    /// </summary>
    /// <param name="request">The route request with locations and costing.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>Result with routing information.</returns>
    Task<Result> GetRouteAsync(RouteRequest request, CancellationToken cancellationToken = default);
    /// <summary>
    /// Request map‑matching (trace_route) from Valhalla.
    /// </summary>
    /// <param name="request">Trace request with GPS points and costing; map matching is enabled automatically.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>Result with matched route.</returns>
    Task<Result> GetMapMatchAsync(TraceRequest request, CancellationToken cancellationToken = default);
    /// <summary>
    /// Locate edges (road segments) near GPS points. Returns way_id (OSM Way ID)
    /// for topology-aware off-route detection.
    /// </summary>
    /// <param name="points">GPS coordinates to locate.</param>
    /// <param name="speedMs">Current speed in m/s. Used for dynamic search radius.</param>
    /// <param name="heading">Current heading in degrees (0-360). Helps Valhalla match correct edge.</param>
    Task<List<LocateResponse?>> LocateAsync(List<(double Lat, double Lon)> points, double speedMs = 0, double heading = 0, CancellationToken cancellationToken = default);
}
