using VegaBridgeApp.Models.Closures;
using VegaBridgeApp.Models.Valhalla;

namespace VegaBridgeApp.Services.Closures;

/// <summary>
/// A single road-closure data source (Overpass/OSM, MobiData BW, …).
/// Providers are discovered via DI and selected by the user in Settings.
/// </summary>
public interface IRoadClosureProvider
{
    /// <summary>Stable identifier, also used as the Settings key (e.g. "overpass", "mobidata").</summary>
    string Key { get; }

    /// <summary>Localized display name shown in Settings (resx key without "ClosureProvider" prefix).</summary>
    string DisplayNameResxKey { get; }

    /// <summary>Whether the provider is available on this platform / configured (always true unless a provider needs an API key).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Queries this source for closures near the given route.
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
        double corridorMeters,
        CancellationToken cancellationToken = default);
}
