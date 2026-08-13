using Serilog;
using VegaBridgeApp.Models.Valhalla;
using VegaBridgeApp.Models.Navigation;
using VegaBridgeApp.Services.Location;
using VegaBridgeApp.Services.Routes;
using VegaBridgeApp.Utils;
using Shiny.Locations;
using VegaBridgeApp.Services.Valhalla;
using Coordinate = VegaBridgeApp.Models.Valhalla.Coordinate;

namespace VegaBridgeApp.Services.Navigation;

/// <summary>
/// Navigation state machine.
/// 
/// Tracks the rider's position along a Valhalla route, determines the current
/// maneuver, calculates distances, and reports state changes through
/// a <see cref="INavigationSink" />.
/// 
/// Works with the screen OFF – the UI is only needed for optional glanceable
/// updates. The core logic runs from GPS callbacks regardless of display state.
/// </summary>
public class NavigationService(GpsService gps, IValhallaClient valhallaClient)
{
    private readonly Lock _lock = new();
    private readonly List<INavigationSink> _sinks = [];
    private Models.Valhalla.Location? _destination;

    /// <summary>
    /// Registers a sink for navigation events. Safe to call multiple times.
    /// </summary>
    public void AddSink(INavigationSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_sinks)
        {
            if (!_sinks.Contains(sink))
                _sinks.Add(sink);
        }
    }

    /// <summary>
    /// of a sink. No-op if sink was not registered.
    /// </summary>
    public void RemoveSink(INavigationSink sink)
    {
        lock (_sinks)
        {
            _sinks.Remove(sink);
        }
    }

    private async Task NotifySinksAsync(Func<INavigationSink, Task> invoke)
    {
        List<INavigationSink> snapshot;
        lock (_sinks)
        {
            snapshot = _sinks.ToList();
        }
        foreach (INavigationSink sink in snapshot)
        {
            try
            {
                await invoke(sink);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "INavigationSink threw an exception");
            }
        }
    }

    // ── Route data (set once per navigation session) ──────────────────────
    private List<Coordinate> _routeCoords = [];
    private double[] _cumulativeDistances = [];
    private List<Maneuver> _maneuvers = [];
    private List<int> _relevantManeuverIndices = []; // Indizes der Nicht-Geradeaus-Manöver (vorab berechnet)
    private int _currentManeuverIndex;
    private bool _isNavigating;

    // ── Session state ────────────────────────────────────────────────────
    private double _distanceToNextTurnM;
    private double _remainingDistanceKm;
    private double _remainingTimeMin;
    private bool _isOffRoute;

    // Smoothing / route-matching state
    private Coordinate? _lastSmoothedPosition;
    private Coordinate? _lastMapMatchedPosition;
    private readonly List<Coordinate> _gpsBuffer = [];
    private int _gpsTickCount;

    private const double OffRouteThresholdDefaultM = 10.0;
    private const int GpsSmoothingWindow = 3;
    private const int RouteLookaheadWindow = 20;
    private const int MapMatchBufferLimit = 5;
    private const int MapMatchTickInterval = 3;
    private const int OffRouteHysteresisCount = 3;
    private int _offRouteCounter;

    private double OffRouteThresholdM => Preferences.Get("off_route_threshold", OffRouteThresholdDefaultM);

    // ── Properties ───────────────────────────────────────────────────────
    public bool IsNavigating => _isNavigating;
    public int CurrentManeuverIndex => _currentManeuverIndex;
    public int TotalManeuvers => _maneuvers.Count;
    public Maneuver? CurrentManeuver =>
        _maneuvers.Count > _currentManeuverIndex ? _maneuvers[_currentManeuverIndex] : null;
    public double TotalDistanceKm { get; private set; }
    public double TotalTimeMin { get; private set; }

    // ── Public API ───────────────────────────────────────────────────────

    private int GetDisplayManeuverIndex()
    {
        lock (_lock)
        {
            if (!_isNavigating || _maneuvers.Count == 0)
                return 0;

            // Handle invalid index (e.g., -1 after off-route or reroute mismatch)
            if (_currentManeuverIndex < 0)
                return 0;

            if (_currentManeuverIndex >= _maneuvers.Count)
                return _maneuvers.Count - 1;

            // Wenn das aktuelle Manöver NICHT 'Geradeaus' ist, zeigen wir es an.
            if (!IsStraightManeuver(_maneuvers[_currentManeuverIndex]))
            {
                return _currentManeuverIndex;
            }

            // Wenn es 'Geradeaus' ist, suchen wir das nächste relevante (Nicht-Geradeaus) Manöver.
            foreach (int candidateIndex in _relevantManeuverIndices.Where(candidateIndex => candidateIndex > _currentManeuverIndex))
            {
                return candidateIndex;
            }

            // Fallback: Wenn nichts mehr kommt, zeige das letzte Manöver
            return _maneuvers.Count - 1;
        }
    }

    private bool IsStraightManeuver(Maneuver m)
    {
        // Uses shared neutral mapping instead of MV Agusta plugin
        return NavigationConstants.IsStraightManeuver(m.Type);
    }

    /// <summary>
    /// Start navigating along a Valhalla route.
    /// </summary>
    public async Task StartNavigation(
        string mergedShape,
        List<Maneuver> maneuvers,
        double totalDistanceKm,
        double totalTimeMin,
        Models.Valhalla.Location destination)
    {
        bool wasNavigating;
        lock (_lock)
        {
            wasNavigating = _isNavigating;
            if (wasNavigating)
            {
                _isNavigating = false;
                gps.ReadingReceived -= OnGpsReading;
                _currentManeuverIndex = 0;
                _routeCoords = [];
                _maneuvers = [];
            }

            InitializeRouteData(mergedShape, maneuvers, totalDistanceKm, totalTimeMin);
            _destination = destination;
            _isNavigating = true;
            _offRouteCounter = 0;
            _lastMapMatchedPosition = null;
            _gpsTickCount = 0;
            _gpsBuffer.Clear();
        }

        if (wasNavigating)
        {
            await gps.StopTrackingAsync();
        }

        _ = NotifySinksAsync(s => s.OnStartAsync(new NavigationStartInfo
        {
            TotalDistanceKm = TotalDistanceKm,
            TotalTimeMin = TotalTimeMin,
            ManeuverCount = maneuvers.Count
        }));

        await gps.StartTrackingAsync(backgroundMode: true);
        gps.ReadingReceived += OnGpsReading;

        Log.Information(
            "Navigation started: {Distance:F1} km, {Time:F0} min, {Maneuvers} maneuvers, {Points} route points",
            totalDistanceKm, totalTimeMin, maneuvers.Count, _routeCoords.Count);
        FireCurrentManeuver();
    }

    /// <summary>Stop the current navigation session (user cancelled).</summary>
    public async Task StopNavigation()
    {
        bool wasNavigating;
        lock (_lock)
        {
            wasNavigating = _isNavigating;
            if (!wasNavigating) return;

            gps.ReadingReceived -= OnGpsReading;
            _isNavigating = false;
            _currentManeuverIndex = 0;
            _routeCoords = [];
            _maneuvers = [];
            _destination = null;
            _lastMapMatchedPosition = null;
            _offRouteCounter = 0;
        }

        if (wasNavigating)
        {
            await gps.StopTrackingAsync();
            Log.Information("Navigation cancelled by user");
            _ = NotifySinksAsync(s => s.OnCancelAsync());
        }
    }

    /// <summary>
    /// Performs a reroute calculation from the current position to the destination.
    /// </summary>
    public async Task<bool> PerformRerouteAsync(double currentLat, double currentLon)
    {
        if (!_isNavigating || _destination == null) return false;

        try
        {
            // 1. Build Request
            List<Models.Valhalla.Location> locs = [
                new() { Lat = currentLat, Lon = currentLon, Type = "break"},
                _destination
            ];

            RouteRequest request = new()
            {
                Locations = locs,
                Costing = "motorcycle",
                DirectionsOptions = new DirectionsOptions { Units = "kilometers", Language = "de"}
            };

            // 2. Call Valhalla
            Result result = await valhallaClient.GetRouteAsync(request);

            if (!result.IsSuccess || result.Response == null)
            {
                Log.Warning("Reroute failed: {Error}", result.ErrorMessage);
                return false;
            }

            RouteResponse response = result.Response;

            // 3. Update state
            (string mergedShape, List<Maneuver> maneuvers, double totalKm, double totalMin) =
                PrepareNavigationData(response.Trip?.Legs ?? []);
            if (string.IsNullOrEmpty(mergedShape)) return false;

            lock (_lock)
            {
                Reroute(mergedShape, maneuvers, totalKm, totalMin);
            }

            // 4. Notify sinks
            _ = NotifySinksAsync(s => s.OnRouteUpdatedAsync(response));

            Log.Information("Reroute successful.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reroute calculation error");
            return false;
        }
    }

    /// <summary>
    /// Replace the route mid-navigation after a reroute.
    /// </summary>
    public void Reroute(string mergedShape, List<Maneuver> maneuvers, double totalDistanceKm, double totalTimeMin)
    {
        lock (_lock)
        {
            InitializeRouteData(mergedShape, maneuvers, totalDistanceKm, totalTimeMin);
            _isOffRoute = false;
            _offRouteCounter = 0;
            _lastMapMatchedPosition = null;
        }

        FireCurrentManeuver();
        Log.Information(
            "Route rerouted: {Distance:F1} km, {Time:F0} min, {Maneuvers} maneuvers",
            totalDistanceKm, totalTimeMin, maneuvers.Count);
    }

    /// <summary>
    /// Merges all leg shapes into a single polyline6 string, collecting all maneuvers
    /// with adjusted shape indices.
    /// </summary>
    public (string MergedShape, List<Maneuver> Maneuvers, double TotalKm, double TotalMin)
        PrepareNavigationData(IReadOnlyList<Leg> legs)
    {
        List<Coordinate> allCoords = [];
        List<Maneuver> allManeuvers = [];
        int shapeOffset = 0;

        foreach (Leg leg in legs)
        {
            List<Coordinate>? legCoords = null;
            if (!string.IsNullOrEmpty(leg.Shape))
            {
                legCoords = PolylineEncoder.DecodePolyline6(leg.Shape);
                if (legCoords.Count > 0)
                {
                    if (allCoords.Count > 0)
                        allCoords.AddRange(legCoords.Skip(1));
                    else
                        allCoords.AddRange(legCoords);
                }
            }

            if (leg.Maneuvers != null)
            {
                allManeuvers.AddRange(leg.Maneuvers.Select(m => new Maneuver()
                {
                    Type = m.Type,
                    Instruction = m.Instruction,
                    BeginShapeIndex = m.BeginShapeIndex + shapeOffset,
                    EndShapeIndex = m.EndShapeIndex + shapeOffset,
                    Length = m.Length,
                    Time = m.Time,
                    TravelMode = m.TravelMode,
                    TravelType = m.TravelType,
                    TurnDegree = m.TurnDegree,
                    RoundaboutExitCount = m.RoundaboutExitCount,
                    RoundaboutExit = m.RoundaboutExit,
                    StreetNames = m.StreetNames,
                    VerbalSuccinctInstruction = m.VerbalSuccinctInstruction,
                    Sign = m.Sign
                }));
            }

            // Reuse cached decode for shape offset calculation
            if (legCoords is { Count: > 0 })
                shapeOffset += legCoords.Count - 1;
        }

        string mergedShape = PolylineEncoder.EncodePolyline6(allCoords);
        double totalKm = legs.Sum(l => l.Summary?.Length ?? 0);
        double totalMin = legs.Sum(l => l.Summary?.Time ?? 0) / 60.0;

        return (mergedShape, allManeuvers, totalKm, totalMin);
    }

    private void InitializeRouteData(string mergedShape, List<Maneuver> maneuvers, double totalDistanceKm, double totalTimeMin)
    {
        _routeCoords = PolylineEncoder.DecodePolyline6(mergedShape);
        _maneuvers = maneuvers;
        UpdateCumulativeDistances();

        // 1. Vorab berechnete Liste der Indizes relevanter Manöver (Nicht-Geradeaus)
        _relevantManeuverIndices = maneuvers
            .Select((m, i) => (m, i))
            .Where(x => !IsStraightManeuver(x.m))
            .Select(x => x.i)
            .ToList();

        // 2. Füge Indizes aller Geradeaus-Manöver am Ende hinzu
        _relevantManeuverIndices.AddRange(maneuvers
            .Select((m, i) => (m, i))
            .Where(x => IsStraightManeuver(x.m))
            .Select(x => x.i));

        TotalDistanceKm = totalDistanceKm;
        TotalTimeMin = totalTimeMin;
        _currentManeuverIndex = 0;
        _remainingDistanceKm = totalDistanceKm;
        _remainingTimeMin = totalTimeMin;
    }

    // ── GPS event handler (core loop) ────────────────────────────────────
    private void OnGpsReading(GpsReading reading)
    {
        lock (_lock)
        {
            if (!_isNavigating || _routeCoords.Count < 2) return;

            // 0. Simple moving average smoothing over the last few raw readings.
            double smoothLat = reading.Position.Latitude;
            double smoothLon = reading.Position.Longitude;
            if (_lastSmoothedPosition != null)
            {
                const double alpha = 0.4;
                smoothLat = alpha * reading.Position.Latitude + (1.0 - alpha) * _lastSmoothedPosition.Value.Latitude;
                smoothLon = alpha * reading.Position.Longitude + (1.0 - alpha) * _lastSmoothedPosition.Value.Longitude;
            }
            _lastSmoothedPosition = new Coordinate(smoothLat, smoothLon, null);

            // Update buffer for map-matching
            _gpsTickCount++;
            _gpsBuffer.Add(new Coordinate(smoothLat, smoothLon, null));
            if (_gpsBuffer.Count > MapMatchBufferLimit) _gpsBuffer.RemoveAt(0);

            // Prefer map-matched position for navigation if available, else smoothed GPS
            double navLat = _lastMapMatchedPosition?.Latitude ?? smoothLat;
            double navLon = _lastMapMatchedPosition?.Longitude ?? smoothLon;

            // 1. Snap current position to route (for UI/Maneuvers)
            int hintIndex = _currentManeuverIndex > 0
                ? _maneuvers[_currentManeuverIndex].BeginShapeIndex
                : 0;

            (int snappedIndex, double distanceMeters) = FindNearestRouteIndexWithLookahead(
                navLat, navLon, hintIndex, RouteLookaheadWindow);

            if (snappedIndex < 0) return;

            // Periodic Off-Route Verification via API
            if (_gpsTickCount % MapMatchTickInterval == 0)
            {
                _ = VerifyRouteAsync();
            }

            int newManeuverIndex = FindManeuverForShapeIndex(snappedIndex);

            int displayIndex = GetDisplayManeuverIndex();

            bool maneuverChanged = newManeuverIndex != _currentManeuverIndex;

            if (maneuverChanged)
            {
                _currentManeuverIndex = newManeuverIndex;
                displayIndex = GetDisplayManeuverIndex();

                if (_currentManeuverIndex >= _maneuvers.Count)
                {
                    Log.Information("Destination reached!");
                    gps.ReadingReceived -= OnGpsReading;
                    _isNavigating = false;
                    _ = Task.Run(async () =>
                    {
                        await gps.StopTrackingAsync();
                        _ = NotifySinksAsync(s => s.OnFinishAsync());
                    });
                    return;
                }
                FireCurrentManeuver(displayIndex);
            }

            _distanceToNextTurnM = CalculateDistanceToNextTurn(snappedIndex);
            (double remainingKm, double remainingMin) = CalculateRemaining(snappedIndex);
            _remainingDistanceKm = remainingKm;
            _remainingTimeMin = remainingMin;

            double speedKmh = reading.Speed * 3.6;

            NavigationStatus status = new()
            {
                SpeedKmh = speedKmh,
                DistanceToNextTurnM = _distanceToNextTurnM,
                RemainingDistanceKm = _remainingDistanceKm,
                RemainingTimeMin = _remainingTimeMin,
                CurrentManeuverIndex = _currentManeuverIndex,
                DisplayManeuverIndex = displayIndex,
                TotalManeuvers = _maneuvers.Count,
                Heading = reading.Heading,
                Accuracy = reading.PositionAccuracy,
                IsStationary = reading.IsStationary
            };
            _ = NotifySinksAsync(s => s.OnStatusAsync(status));
        }
    }

    private void UpdateCumulativeDistances()
    {
        if (_routeCoords.Count < 2)
        {
            _cumulativeDistances = [];
            return;
        }

        _cumulativeDistances = new double[_routeCoords.Count];
        double total = 0;
        for (int i = 1; i < _routeCoords.Count; i++)
        {
            total += GeoMath.DistanceMeters(
                _routeCoords[i - 1].Latitude, _routeCoords[i - 1].Longitude,
                _routeCoords[i].Latitude, _routeCoords[i].Longitude);
            _cumulativeDistances[i] = total;
        }
    }

    // ── Route matching ───────────────────────────────────────────────────
    private (int Index, double DistanceMeters) FindNearestRouteIndexWithLookahead(
        double lat, double lon, int hintIndex, int window)
    {
        if (_routeCoords.Count == 0)
            return (-1, double.MaxValue);

        int start = Math.Max(0, hintIndex - window);
        int end = Math.Min(_routeCoords.Count - 1, hintIndex + window);

        int bestIndex = start;
        double bestDistSq = double.MaxValue;

        for (int i = start; i < end; i++)
        {
            double distSq = PointToSegmentDistanceSq(
                lon, lat,
                _routeCoords[i].Longitude, _routeCoords[i].Latitude,
                _routeCoords[i + 1].Longitude, _routeCoords[i + 1].Latitude,
                out double t);

            if (!(distSq < bestDistSq)) continue;
            bestDistSq = distSq;
            bestIndex = t >= 0.5 ? i + 1 : i;
        }

        const double FallbackThresholdSq = 50.0 * 50.0;
        if (bestDistSq > FallbackThresholdSq)
        {
            bestDistSq = double.MaxValue;
            bestIndex = 0;

            for (int i = 0; i < _routeCoords.Count - 1; i++)
            {
                double distSq = PointToSegmentDistanceSq(
                    lon, lat,
                    _routeCoords[i].Longitude, _routeCoords[i].Latitude,
                    _routeCoords[i + 1].Longitude, _routeCoords[i + 1].Latitude,
                    out double t);

                if (!(distSq < bestDistSq)) continue;
                bestDistSq = distSq;
                bestIndex = t >= 0.5 ? i + 1 : i;
            }
        }

        double distanceMeters = GeoMath.DistanceMeters(
            _routeCoords[bestIndex].Latitude, _routeCoords[bestIndex].Longitude,
            lat, lon);
        return (bestIndex, distanceMeters);
    }

    private static double PointToSegmentDistanceSq(
        double px, double py,
        double ax, double ay,
        double bx, double by,
        out double t)
    {
        double dx = bx - ax;
        double dy = by - ay;

        if (Math.Abs(dx) < 1e-12 && Math.Abs(dy) < 1e-12)
        {
            t = 0;
            double ex = px - ax;
            double ey = py - ay;
            return ex * ex + ey * ey;
        }
        t = ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0, 1);
        double closestX = ax + t * dx;
        double closestY = ay + t * dy;
        double exx = px - closestX;
        double eyy = py - closestY;

        return exx * exx + eyy * eyy;
    }

    private int FindManeuverForShapeIndex(int routeIndex)
    {
        for (int i = 0; i < _maneuvers.Count; i++)
        {
                if (routeIndex >= _maneuvers[i].BeginShapeIndex &&
                    routeIndex < _maneuvers[i].EndShapeIndex)
            {
                return i;
            }
        }
        return -1;
    }

    // TODO: integrate Valhalla map‑matching confidence weighting
    private double CalculateDistanceToNextTurn(int currentRouteIndex)
    {
        if (_currentManeuverIndex >= _maneuvers.Count) return 0;

        int targetIndex = _maneuvers[_currentManeuverIndex].EndShapeIndex;
        if (targetIndex >= _routeCoords.Count)
            targetIndex = _routeCoords.Count - 1;

        if (currentRouteIndex >= _cumulativeDistances.Length - 1) return 0;
        return _cumulativeDistances[targetIndex] - _cumulativeDistances[currentRouteIndex];
    }

    private (double RemainingKm, double RemainingMin) CalculateRemaining(int currentRouteIndex)
    {
        if (currentRouteIndex >= _routeCoords.Count - 1)
            return (0, 0);

        double remainingM = _cumulativeDistances[^1] - _cumulativeDistances[currentRouteIndex];
        double remainingKm = remainingM / 1000.0;
        double fractionRemaining = TotalDistanceKm > 0
            ? remainingKm / TotalDistanceKm
            : 1;
        double remainingMin = TotalTimeMin * fractionRemaining;

        return (remainingKm, remainingMin);
    }

    private async Task VerifyRouteAsync()
    {
        List<Coordinate> bufferSnapshot;
        lock (_lock)
        {
            if (!_isNavigating) return;
            bufferSnapshot = _gpsBuffer.ToList();
        }

        if (bufferSnapshot.Count < 2) return;

        try
        {
            TraceRequest request = new()
            {
                Locations = bufferSnapshot.Select(p => new double[] { p.Latitude, p.Longitude }).ToList(),
                Costing = "motorcycle"
            };
            request.CostingOptions ??= new();
            request.CostingOptions["shape_match"] = "map_snap";

            Result result = await valhallaClient.GetMapMatchAsync(request);

            if (!result.IsSuccess || result.Response == null) return;

            RouteResponse response = result.Response;
            var snappedTrip = response.Trip;
            if (snappedTrip == null || snappedTrip.Legs == null || snappedTrip.Legs.Count == 0) return;

            Coordinate lastSnapped = PolylineEncoder.DecodePolyline6(snappedTrip.Legs[0].Shape).Last();

            (int _, double distToRoute) = FindNearestRouteIndexWithLookahead(
                lastSnapped.Latitude, lastSnapped.Longitude, 0, RouteLookaheadWindow);

            lock (_lock)
            {
                if (distToRoute > OffRouteThresholdM)
                {
                    _offRouteCounter++;
                    if (_offRouteCounter >= OffRouteHysteresisCount && !_isOffRoute)
                    {
                        _isOffRoute = true;
                        Log.Warning("Off-Route verified by Map-Matching: {Dist:F1}m", distToRoute);
                        _ = NotifySinksAsync(s => s.OnOffRouteAsync(lastSnapped.Latitude, lastSnapped.Longitude, distToRoute));
                    }
                }
                else
                {
                    _offRouteCounter = 0;
                    if (_isOffRoute)
                    {
                        _isOffRoute = false;
                        Log.Information("Back on route (verified by Map-Matching)");
                    }
                    
                    // Store map-matched position for navigation
                    _lastMapMatchedPosition = lastSnapped;
                    Log.Debug("Map-matched position updated: {Lat}, {Lon}", lastSnapped.Latitude, lastSnapped.Longitude);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during route verification");
        }
    }

    // ── Maneuver change notification ─────────────────────────────────────
    // Note: map‑matching could be used here to refine upcoming maneuver info
    private void FireCurrentManeuver(int index = -1)
    {
        int targetIndex = index != -1 ? index : GetDisplayManeuverIndex();
        
        if (targetIndex < 0 || targetIndex >= _maneuvers.Count) return;

        Maneuver? m = _maneuvers[targetIndex];
        if (m == null) return;

        NavigationManeuverInfo info = new()
        {
            Index = targetIndex,
            Total = _maneuvers.Count,
            ValhallaType = m.Type,
            Instruction = m.Instruction ?? "",
            StreetNames = m.StreetNames ?? [],
            LengthKm = m.Length,
            TimeMin = m.Time / 60.0,
            TurnDegree = m.TurnDegree,
            RoundaboutExitCount = m.RoundaboutExitCount,
            TravelMode = m.TravelMode,
            TravelType = m.TravelType,
            RoundaboutExit = m.RoundaboutExit
        };

        Log.Debug(
            "Maneuver Display {I}/{T}: {Instr} (ValhallaType={Type})",
            targetIndex + 1, _maneuvers.Count,
            m.Instruction, m.Type);

        _ = NotifySinksAsync(s => s.OnManeuverAsync(info));
    }
}
