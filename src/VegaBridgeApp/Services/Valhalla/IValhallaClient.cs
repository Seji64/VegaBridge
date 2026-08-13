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
    /// <param name="request">Route request; set CostingOptions["shape_match"]="map_snap" for map matching.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>Result with matched route.</returns>
    Task<Result> GetMapMatchAsync(TraceRequest request, CancellationToken cancellationToken = default);
}
