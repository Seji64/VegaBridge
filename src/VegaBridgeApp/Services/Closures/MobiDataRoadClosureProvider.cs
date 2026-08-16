using System.Globalization;
using System.Text.Json;
using Flurl.Http;
using Serilog;
using VegaBridgeApp.Models.Closures;
using VegaBridgeApp.Models.Valhalla;
using VegaBridgeApp.Utils;

namespace VegaBridgeApp.Services.Closures;

/// <summary>
/// Road-closure check via the official Baden-Württemberg roadworks feed
/// (MobiData BW, Ministerium für Verkehr / Straßenbauverwaltung).
///
/// Source: https://api.mobidata-bw.de/datasets/traffic/roadworks/roadworks_geojson.json
/// A GeoJSON FeatureCollection (LineStrings) of planned/active roadworks on
/// federal (B), state (L) and county (K) roads – the same data shown on
/// mobidata-bw.de and aggregated by Google Maps.
///
/// Strategy:
/// 1. Download the feed once and cache it in memory (CacheDuration).
/// 2. Filter features to those currently valid (start &lt;= now &lt;= end).
/// 3. Keep only features whose geometry actually runs along the route
///    corridor (point-to-route distance), sorted nearest first.
/// </summary>
public class MobiDataRoadClosureProvider : IRoadClosureProvider
{
    internal const string HttpClientName = "MobiDataBW";
    internal const int RetryCount = 2;
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

    /// <inheritdoc />
    public string Key => "mobidata";

    /// <inheritdoc />
    public string DisplayNameResxKey => "ClosureProviderMobiData";

    /// <inheritdoc />
    public bool IsAvailable => true;

    private readonly FlurlClient _flurlClient;
    private List<MobiDataFeature>? _cachedFeatures;
    private DateTimeOffset _cacheExpires = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public MobiDataRoadClosureProvider(IHttpClientFactory httpClientFactory)
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

            List<MobiDataFeature> features = await GetFeaturesAsync(cancellationToken);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<RoadClosure> closures = [];

            foreach (MobiDataFeature feature in features)
            {
                MobiDataProperties? props = feature.Properties;
                if (props is null || feature.Geometry?.Coordinates is not { Count: > 1 } coords)
                    continue;

                // 1. Validity window: only currently active roadworks.
                if (!TryParseTime(props.StartTime, out DateTimeOffset start) ||
                    !TryParseTime(props.EndTime, out DateTimeOffset end))
                    continue;
                if (now < start || now > end)
                    continue;

                // 2. Corridor check over the full LineString geometry: the
                //    roadworks must actually lie on the route.
                double minDist = double.MaxValue;
                (double Lat, double Lon) nearest = (0, 0);
                bool anyNear = false;
                for (int i = 0; i < coords.Count; i++)
                {
                    // GeoJSON coordinates are [lon, lat].
                    double lon = coords[i][0];
                    double lat = coords[i][1];
                    double d = DistanceToRoute(lat, lon, routeCoords);
                    if (d < minDist)
                    {
                        minDist = d;
                        nearest = (lat, lon);
                    }
                    if (d <= corridorMeters)
                        anyNear = true;
                }

                if (!anyNear)
                    continue;

                // 3. Build the closure entry – nearest geometry point as
                //    position, description as name, validity end as "until".
                ClosureKind kind = props.Type?.Contains("ROAD_CLOSED", StringComparison.OrdinalIgnoreCase) == true
                    ? ClosureKind.Construction
                    : ClosureKind.Roadworks;

                long id = StableId(props.Id);
                closures.Add(new RoadClosure(
                    id,
                    kind,
                    props.Street ?? props.Description,
                    null,
                    nearest.Lat,
                    nearest.Lon,
                    end)
                {
                    Source = Key
                });
            }

            Log.Information("MobiData BW check: {Count} closure(s) within {Corridor:F0}m corridor",
                closures.Count, corridorMeters);

            return new RoadClosureCheckResult(
                closures
                    .OrderBy(c => DistanceToRoute(c.Latitude, c.Longitude, routeCoords))
                    .ToList(),
                DateTimeOffset.UtcNow,
                IsSuccess: true);
        }
        catch (OperationCanceledException)
        {
            Log.Debug("MobiData BW check cancelled");
            return RoadClosureCheckResult.Failure("Cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MobiData BW closure check failed");
            return RoadClosureCheckResult.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Downloads the feed once per <see cref="CacheDuration"/> and returns the
    /// cached features. Thread-safe.
    /// </summary>
    private async Task<List<MobiDataFeature>> GetFeaturesAsync(CancellationToken cancellationToken)
    {
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedFeatures != null && DateTimeOffset.UtcNow < _cacheExpires)
                return _cachedFeatures;

            Log.Information("Downloading MobiData BW roadworks feed");
            string body = await _flurlClient
                .Request("datasets/traffic/roadworks/roadworks_geojson.json")
                .GetStringAsync(cancellationToken: cancellationToken);

            MobiDataFeed? feed;
            try
            {
                feed = JsonSerializer.Deserialize<MobiDataFeed>(body);
            }
            catch (JsonException ex)
            {
                Log.Warning(ex, "MobiData BW returned malformed JSON");
                _cachedFeatures = [];
                _cacheExpires = DateTimeOffset.UtcNow + CacheDuration;
                return _cachedFeatures;
            }

            _cachedFeatures = feed?.Features ?? [];
            _cacheExpires = DateTimeOffset.UtcNow + CacheDuration;
            Log.Information("MobiData BW feed cached: {Count} features", _cachedFeatures.Count);
            return _cachedFeatures;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private static bool TryParseTime(string? value, out DateTimeOffset result)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
        {
            return true;
        }
        result = default;
        return false;
    }

    /// <summary>
    /// Stable numeric id from the provider's string id (e.g.
    /// "117635355-…-sperrung.001") – the RoadClosure model uses a long id.
    /// FNV-1a 64-bit over the string, masked to positive.
    /// </summary>
    private static long StableId(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return 0;
        ulong hash = 14695981039346656037UL;
        foreach (char c in id)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return (long)(hash & 0x7FFFFFFFFFFFFFFFUL);
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
