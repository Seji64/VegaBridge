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

    /// <summary>
    /// Hard timeout per provider. Providers that are slower (e.g. Overpass
    /// 504s) are cancelled so their result is not delivered, while faster
    /// providers (MobiData from disk cache) still report their closures.
    /// </summary>
    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(12);

    public RoadClosureService(IEnumerable<IRoadClosureProvider> providers)
    {
        _providers = providers.Where(p => p.IsAvailable).ToList();
        _byKey = _providers.ToDictionary(p => p.Key);
    }

    /// <summary>All available providers (for the Settings UI).</summary>
    public IReadOnlyList<IRoadClosureProvider> Providers => _providers;

    /// <summary>
    /// Provider keys enabled by default when the user has not changed the
    /// selection yet. Only Overpass is active by default – MobiData BW is
    /// an opt-in (its feed download adds latency).
    /// </summary>
    private static readonly string[] DefaultEnabledKeys = ["overpass"];

    /// <summary>
    /// Returns the keys of the providers the user has enabled in Settings.
    /// Defaults to <see cref="DefaultEnabledKeys"/> when NOTHING was ever
    /// persisted (first launch). An explicitly stored empty string means the
    /// user deliberately disabled ALL providers – that must NOT fall back to
    /// the default, otherwise a provider can never be switched off.
    /// </summary>
    public IReadOnlyList<string> GetEnabledProviderKeys()
    {
        string? stored = Preferences.Default.Get(PreferencesKey, (string?)null);
        if (stored is null)
            return _providers
                .Where(p => DefaultEnabledKeys.Contains(p.Key))
                .Select(p => p.Key)
                .ToList();

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

        // Run the enabled providers in parallel with a hard per-provider
        // timeout – Overpass (2 requests) can stall (504) while MobiData
        // (disk cache) answers in ~1s. A slow provider is cancelled so its
        // absence does not block the closures the fast provider found.
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProviderTimeout);

        List<(string Key, Task<RoadClosureCheckResult> Task)> pending = enabledKeys
            .Where(k => _byKey.ContainsKey(k))
            .Select(k => (k, _byKey[k].CheckRouteAsync(routeCoords, corridorMeters, timeoutCts.Token)))
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
                // Only propagate cancellation when the CALLER cancelled
                // (page closed, navigation stopped). Our per-provider
                // timeout just means this provider was too slow – its
                // absence must not block the other providers' results.
                if (cancellationToken.IsCancellationRequested)
                    throw;
                Log.Warning("Road closure provider {Key} timed out after {Timeout}", key, ProviderTimeout);
                failures.Add($"{key}: timed out");
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
