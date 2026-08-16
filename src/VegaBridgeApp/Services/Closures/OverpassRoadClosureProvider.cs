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
///    inside that box (two-stage: cheap center pass, then geometry only
///    for the candidates).
/// 3. Filter results to the corridor (point-to-route distance), sort nearest first.
///
/// Uses a named <c>HttpClient</c> (registered via <c>AddHttpClient</c> +
/// <c>AddResilienceHandler</c>) so retry and timeout are handled at the
/// transport layer. Flurl provides the convenient JSON request syntax –
/// exactly the same pattern as the Valhalla client.
/// </summary>
public class OverpassRoadClosureProvider : IRoadClosureProvider
{
    internal const string HttpClientName = "Overpass";
    internal const int RetryCount = 2;

    /// <inheritdoc />
    public string Key => "overpass";

    /// <inheritdoc />
    public string DisplayNameResxKey => "ClosureProviderOverpass";

    /// <inheritdoc />
    public bool IsAvailable => true;

    private const int MaxQueryAreaDegreesSq = 8; // ~ 800 km² guard against oversized boxes

    /// <summary>
    /// Minimum length (in meters) that a closure way must run alongside the
    /// route to count as a closure ON the route. Ways that merely cross the
    /// route (farm tracks, driveways with access=no) span less than this.
    /// </summary>
    private const double MinOverlapMeters = 30;

    /// <summary>
    /// Highway tags that are not drivable by motorcycle – closures on these
    /// are irrelevant for the route check.
    /// </summary>
    private static readonly HashSet<string> NonDrivableHighwayTags =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "path", "footway", "cycleway", "steps", "bridleway",
            "pedestrian", "corridor", "track"
        };
    private readonly FlurlClient _flurlClient;

    public RoadClosureService(IHttpClientFactory httpClientFactory)
    {
        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
        _flurlClient = new FlurlClient(httpClient);
    }

    /// <inheritdoc />
    public async Task<RoadClosureCheckResult> CheckRouteAsync(
        IReadOnlyList<Coordinate> routeCoords,
        double corridorMeters,
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

            string bbox = string.Create(CultureInfo.InvariantCulture,
                $"{minLat:F6},{minLon:F6},{maxLat:F6},{maxLon:F6}");

            // 2. Two-stage query:
            //    a) Cheap `out center` pass over the bbox to get candidate
            //       ways with their tags (no geometry – fast even for long
            //       routes, avoids Overpass timeouts).
            //    b) Targeted `out geom` for exactly those candidate ids so
            //       the expensive geometry is only fetched for candidates.
            string candidateQuery = $"""
                [out:json][timeout:20];
                (
                  way["highway"="construction"]({bbox});
                  way["highway"]["construction"]({bbox});
                  way["access"="no"]({bbox});
                  way["motor_vehicle"="no"]({bbox});
                  way["barrier"~"^(gate|bollard|lift_gate|swing_gate|block)$"]({bbox});
                );
                out center tags;
                """;

            Log.Debug("Overpass closure candidate query: {Query}", candidateQuery.Replace('\n', ' '));

            string candidateBody = await _flurlClient
                .Request("api/interpreter")
                .PostUrlEncodedAsync(new { data = candidateQuery }, cancellationToken: cancellationToken)
                .ReceiveString();

            List<OverpassElement> candidates = ParseCandidates(candidateBody);
            if (candidates.Count == 0)
            {
                Log.Information("Closure check: no candidates in bounding box");
                return new RoadClosureCheckResult([], DateTimeOffset.UtcNow, IsSuccess: true);
            }

            // b) Geometry for the candidates only – the corridor filter runs
            //    on the real way geometry in ParseClosures.
            string ids = string.Join(',', candidates.Select(c => c.Id));
            string geometryQuery = $"""
                [out:json][timeout:20];
                way(id:{ids});
                out geom tags;
                """;

            Log.Debug("Overpass closure geometry query ({Count} ways): {Query}", candidates.Count, geometryQuery.Replace('\n', ' '));

            string geometryBody = await _flurlClient
                .Request("api/interpreter")
                .PostUrlEncodedAsync(new { data = geometryQuery }, cancellationToken: cancellationToken)
                .ReceiveString();

            List<RoadClosure> closures = ParseClosures(geometryBody, routeCoords, corridorMeters);

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

    /// <summary>
    /// Parses the cheap `out center tags` response and keeps only elements
    /// that have a center and a drivable highway tag (if any highway tag
    /// is present). The corridor filter itself runs later on the geometry.
    /// </summary>
    private static List<OverpassElement> ParseCandidates(string responseBody)
    {
        List<OverpassElement> candidates = [];

        OverpassResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<OverpassResponse>(responseBody);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Overpass returned malformed JSON");
            return candidates;
        }

        if (response?.Elements is null)
            return candidates;

        foreach (OverpassElement el in response.Elements)
        {
            if (el.Type != "way" || el.Center is null)
                continue;

            // Only drivable ways matter for a motorcycle route. Footpaths,
            // cycleways, steps and forest tracks with access=no are common
            // OSM tags but irrelevant for the rider.
            string? highway = el.Tags?.Highway;
            if (!string.IsNullOrEmpty(highway) &&
                NonDrivableHighwayTags.Contains(highway))
                continue;

            candidates.Add(el);
        }

        return candidates;
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
            if (el.Type != "way" || el.Geometry is not { Count: > 0 } geometry)
                continue;

            // Only drivable ways matter for a motorcycle route. Footpaths,
            // cycleways, steps and forest tracks with access=no are common
            // OSM tags but irrelevant for the rider.
            string? highway = el.Tags?.Highway;
            if (!string.IsNullOrEmpty(highway) &&
                NonDrivableHighwayTags.Contains(highway))
                continue;

            // Corridor filter over the FULL way geometry (not just the
            // center): the closure must actually touch the route.
            // Points of the way that lie within the corridor, in way order.
            List<(OverpassGeometryPoint Point, double DistM)> near = [];
            double minDist = double.MaxValue;
            OverpassGeometryPoint? nearest = null;
            foreach (OverpassGeometryPoint p in geometry)
            {
                double d = DistanceToRoute(p.Lat, p.Lon, routeCoords);
                if (d <= corridorMeters)
                    near.Add((p, d));
                if (d < minDist)
                {
                    minDist = d;
                    nearest = p;
                }
            }

            if (near.Count == 0 || nearest == null)
                continue;

            // A way that merely CROSSES the route (e.g. an access=no farm
            // track intersecting the road) has only a single point within
            // the corridor. Only closures that RUN ALONG the route matter:
            // require the near points to span a meaningful length.
            double overlapM = 0;
            for (int i = 1; i < near.Count; i++)
            {
                overlapM += GeoMath.DistanceMeters(
                    near[i - 1].Point.Lat, near[i - 1].Point.Lon,
                    near[i].Point.Lat, near[i].Point.Lon);
            }
            if (overlapM < MinOverlapMeters)
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

            // Position = the geometry point closest to the route (accurate
            // location for the map highlight, not the way center).
            closures.Add(new RoadClosure(
                el.Id,
                kind,
                el.Tags?.Name,
                el.Tags?.Highway,
                nearest.Lat,
                nearest.Lon,
                lastModified)
            {
                Source = Key
            });
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
