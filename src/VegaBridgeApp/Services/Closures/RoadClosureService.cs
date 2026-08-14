using System.Globalization;
using System.Text.Json;
using Flurl.Http;
using Serilog;
using VegaBridgeApp.Models.Closures;
using VegaBridgeApp.Models.Valhalla;
using VegaBridgeApp.Utils;

namespace VegaBridgeApp.Services.Closures;

/// <summary>
/// Road-closure check via the Overpass API (live OpenStreetMap data).
///
/// Strategy:
/// 1. Build the route's bounding box + corridor margin.
/// 2. Query Overpass for construction / access-restricted / barrier ways
///    inside that box (single request, `out center`).
/// 3. Filter results to the corridor (point-to-route distance), sort nearest first.
///
/// Uses a named <c>HttpClient</c> (registered via <c>AddHttpClient</c> +
/// <c>AddResilienceHandler</c>) so retry and timeout are handled at the
/// transport layer. Flurl provides the convenient JSON request syntax –
/// exactly the same pattern as the Valhalla client.
/// </summary>
public class RoadClosureService : IRoadClosureService
{
    internal const string HttpClientName = "Overpass";
    internal const int RetryCount = 2;

    private const int MaxQueryAreaDegreesSq = 8; // ~ 800 km² guard against oversized boxes
    private readonly FlurlClient _flurlClient;

    public RoadClosureService(IHttpClientFactory httpClientFactory)
    {
        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
        _flurlClient = new FlurlClient(httpClient);
    }

    /// <inheritdoc />
    public async Task<RoadClosureCheckResult> CheckRouteAsync(
        IReadOnlyList<Coordinate> routeCoords,
        double corridorMeters = 200,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (routeCoords is null || routeCoords.Count < 2)
                return RoadClosureCheckResult.Failure("Route needs at least 2 points");

            // 1. Bounding box of the whole route, expanded by the corridor.
            double minLat = routeCoords.Min(p => p.Latitude);
            double maxLat = routeCoords.Max(p => p.Latitude);
            double minLon = routeCoords.Min(p => p.Longitude);
            double maxLon = routeCoords.Max(p => p.Longitude);

            double margin = corridorMeters / 111_000.0; // approx degrees for the corridor
            minLat -= margin;
            maxLat += margin;
            minLon -= margin / Math.Max(0.1, Math.Cos(maxLat * Math.PI / 180));
            maxLon += margin / Math.Max(0.1, Math.Cos(maxLat * Math.PI / 180));

            double areaDegSq = (maxLat - minLat) * (maxLon - minLon);
            if (areaDegSq > MaxQueryAreaDegreesSq)
            {
                Log.Warning("Route bounding box too large for Overpass ({Area:F1} deg²), skipping check", areaDegSq);
                return RoadClosureCheckResult.Failure("Route area too large for a closure check");
            }

            // 2. Overpass query: construction ways, roads with construction=*,
            //    access-restricted ways, and physical barriers.
            string bbox = string.Create(CultureInfo.InvariantCulture,
                $"{minLat:F6},{minLon:F6},{maxLat:F6},{maxLon:F6}");

            string query = $"""
                [out:json][timeout:20];
                (
                  way["highway"="construction"]({bbox});
                  way["construction"]({bbox});
                  way["access"="no"]({bbox});
                  way["motor_vehicle"="no"]({bbox});
                  way["barrier"~"^(gate|bollard|lift_gate|swing_gate|block)$"]({bbox});
                );
                out center;
                """;

            Log.Debug("Overpass closure query: {Query}", query.Replace('\n', ' '));

            // 3. POST via Flurl (form-encoded `data` parameter), like the
            //    Valhalla client posts JSON. Polly retries transient failures.
            string responseBody = await _flurlClient
                .Request("api/interpreter")
                .PostUrlEncodedAsync(new { data = query }, cancellationToken: cancellationToken)
                .ReceiveString();

            List<RoadClosure> closures = ParseClosures(responseBody, routeCoords, corridorMeters);

            Log.Information("Closure check: {Count} closure(s) within {Corridor:F0}m corridor",
                closures.Count, corridorMeters);

            return new RoadClosureCheckResult(
                closures,
                DateTimeOffset.UtcNow,
                IsSuccess: true);
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Closure check cancelled");
            return RoadClosureCheckResult.Failure("Cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Closure check failed");
            return RoadClosureCheckResult.Failure(ex.Message);
        }
    }

    private List<RoadClosure> ParseClosures(string responseBody, IReadOnlyList<Coordinate> routeCoords, double corridorMeters)
    {
        List<RoadClosure> closures = [];

        OverpassResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<OverpassResponse>(responseBody);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Overpass returned malformed JSON");
            return closures;
        }

        if (response?.Elements is null)
            return closures;

        foreach (OverpassElement el in response.Elements)
        {
            if (el.Type != "way" || el.Center is null)
                continue;

            double lat = el.Center.Lat;
            double lon = el.Center.Lon;

            // Corridor filter: keep only closures close enough to the route.
            double distM = DistanceToRoute(lat, lon, routeCoords);
            if (distM > corridorMeters)
                continue;

            ClosureKind kind = ClosureKind.Access;
            if (el.Tags is { } tags)
            {
                kind = ClassifyKind(tags);
            }

            DateTimeOffset? lastModified = null;
            if (!string.IsNullOrWhiteSpace(el.Timestamp) &&
                DateTimeOffset.TryParse(el.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset ts))
            {
                lastModified = ts;
            }

            closures.Add(new RoadClosure(
                el.Id,
                kind,
                el.Tags?.Name,
                el.Tags?.Highway,
                lat,
                lon,
                lastModified));
        }

        // Nearest first.
        return closures
            .OrderBy(c => DistanceToRoute(c.Latitude, c.Longitude, routeCoords))
            .ToList();
    }

    private static ClosureKind ClassifyKind(OverpassTags tags)
    {
        if (tags.Highway == "construction")
            return ClosureKind.Construction;
        if (!string.IsNullOrEmpty(tags.Construction))
            return ClosureKind.Roadworks;
        if (!string.IsNullOrEmpty(tags.Barrier))
            return ClosureKind.Barrier;
        return ClosureKind.Access;
    }

    /// <summary>
    /// Minimum distance from a point to the route polyline (haversine on
    /// segment endpoints, planar approximation for short segments is fine).
    /// </summary>
    private static double DistanceToRoute(double lat, double lon, IReadOnlyList<Coordinate> route)
    {
        double best = double.MaxValue;
        for (int i = 0; i < route.Count - 1; i++)
        {
            Coordinate a = route[i];
            Coordinate b = route[i + 1];
            best = Math.Min(best, PointToSegmentDistanceMeters(lat, lon, a.Latitude, a.Longitude, b.Latitude, b.Longitude));
            if (best < 1e-6) break;
        }
        return best;
    }

    private static double PointToSegmentDistanceMeters(
        double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;

        double t;
        if (Math.Abs(dx) < 1e-12 && Math.Abs(dy) < 1e-12)
        {
            t = 0;
        }
        else
        {
            t = ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy);
            t = Math.Clamp(t, 0, 1);
        }

        double closestLat = ax + t * dx;
        double closestLon = ay + t * dy;
        return GeoMath.DistanceMeters(px, py, closestLat, closestLon);
    }
}
