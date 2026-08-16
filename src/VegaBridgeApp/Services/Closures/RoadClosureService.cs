using Serilog;
using VegaBridgeApp.Models.Closures;
using VegaBridgeApp.Models.Valhalla;
using VegaBridgeApp.Utils;

namespace VegaBridgeApp.Services.Closures;

/// <summary>
/// Aggregates all configured <see cref="IRoadClosureProvider"/>s and merges
/// their results. The active providers are selected by the user in Settings
/// (persisted in Preferences) – so additional providers can be added later
/// by implementing <see cref="IRoadClosureProvider"/> and registering them
/// in DI, without touching this class or the UI.
/// </summary>
public class RoadClosureService : IRoadClosureService
{
    private const string PreferencesKey = "closure_provider_enabled";

    private readonly IReadOnlyList<IRoadClosureProvider> _providers;
    private readonly IReadOnlyDictionary<string, IRoadClosureProvider> _byKey;

    /// <summary>Default corridor used when the caller does not specify one.</summary>
    public const double DefaultCorridorMeters = 15;

    public RoadClosureService(IEnumerable<IRoadClosureProvider> providers)
    {
        _providers = providers.Where(p => p.IsAvailable).ToList();
        _byKey = _providers.ToDictionary(p => p.Key);
    }

    /// <summary>All available providers (for the Settings UI).</summary>
    public IReadOnlyList<IRoadClosureProvider> Providers => _providers;

    /// <summary>
    /// Returns the keys of the providers the user has enabled in Settings.
    /// Defaults to all providers when nothing is persisted yet.
    /// </summary>
    public IReadOnlyList<string> GetEnabledProviderKeys()
    {
        string? stored = Preferences.Default.Get(PreferencesKey, (string?)null);
        if (string.IsNullOrWhiteSpace(stored))
            return _providers.Select(p => p.Key).ToList();

        HashSet<string> enabled = stored
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Only keys that still map to a registered provider.
        return _providers.Where(p => enabled.Contains(p.Key)).Select(p => p.Key).ToList();
    }

    /// <summary>
    /// Persists the enabled provider keys. Unknown keys are ignored.
    /// </summary>
    public void SetEnabledProviderKeys(IEnumerable<string> keys)
    {
        HashSet<string> known = keys
            .Where(k => _byKey.ContainsKey(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Preferences.Default.Set(PreferencesKey, string.Join(',', known));
    }

    /// <inheritdoc />
    public async Task<RoadClosureCheckResult> CheckRouteAsync(
        IReadOnlyList<Coordinate> routeCoords,
        double corridorMeters = DefaultCorridorMeters,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> enabledKeys = GetEnabledProviderKeys();
        if (enabledKeys.Count == 0)
        {
            Log.Information("Road closure check: no provider enabled in Settings");
            return RoadClosureCheckResult.Failure("No road-closure provider enabled in Settings");
        }

        List<RoadClosure> all = [];
        DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
        List<string> failures = [];

        // Run the enabled providers in parallel – Overpass (2 requests) and
        // MobiData (feed download) together can take 20+ s sequentially.
        // Parallel execution cuts the wait to the slowest provider.
        List<(string Key, Task<RoadClosureCheckResult> Task)> pending = enabledKeys
            .Where(k => _byKey.ContainsKey(k))
            .Select(k => (k, _byKey[k].CheckRouteAsync(routeCoords, corridorMeters, cancellationToken)))
            .ToList();

        while (pending.Count > 0)
        {
            // Wait for whichever provider finishes next, so a throwing
            // provider does not discard the other results.
            Task<RoadClosureCheckResult> done = await Task.WhenAny(pending.Select(p => p.Task));
            int idx = pending.FindIndex(p => p.Task == done);
            (string key, Task<RoadClosureCheckResult> task) = pending[idx];
            pending.RemoveAt(idx);

            try
            {
                RoadClosureCheckResult result = await task;
                if (result.IsSuccess)
                    all.AddRange(result.Closures);
                else if (!string.IsNullOrEmpty(result.ErrorMessage))
                    failures.Add($"{key}: {result.ErrorMessage}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Road closure provider {Key} failed", key);
                failures.Add($"{key}: {ex.Message}");
            }
        }

        // Deduplicate by (source, id) – the same closure can appear in
        // multiple feeds (e.g. mobidata and OSM). Keep the nearest.
        Dictionary<(string Source, long Id), RoadClosure> byId = [];
        foreach (RoadClosure closure in all)
        {
            double dist = DistanceToRoute(closure.Latitude, closure.Longitude, routeCoords);
            if (byId.TryGetValue((closure.Source, closure.OsmId), out RoadClosure? existing))
            {
                if (dist < DistanceToRoute(existing.Latitude, existing.Longitude, routeCoords))
                    byId[(closure.Source, closure.OsmId)] = closure;
            }
            else
            {
                byId[(closure.Source, closure.OsmId)] = closure;
            }
        }

        List<RoadClosure> merged = byId.Values
            .OrderBy(c => DistanceToRoute(c.Latitude, c.Longitude, routeCoords))
            .ToList();

        Log.Information("Road closure check: {Count} closure(s) from {Providers} providers",
            merged.Count, string.Join(", ", enabledKeys));

        if (merged.Count == 0 && failures.Count > 0)
        {
            return RoadClosureCheckResult.Failure(string.Join("; ", failures));
        }

        return new RoadClosureCheckResult(merged, checkedAt, IsSuccess: true);
    }

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
