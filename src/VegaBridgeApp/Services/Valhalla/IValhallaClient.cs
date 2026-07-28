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
}
