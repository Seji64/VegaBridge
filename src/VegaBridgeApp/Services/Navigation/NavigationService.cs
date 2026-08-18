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
    /// Removes a sink. No-op if the sink was not registered.
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
    private List<int> _relevantManeuverIndices = []; // indices of non-straight maneuvers (precomputed)
    private int _currentManeuverIndex;
    private bool _isNavigating;

    // Intermediate waypoints of the active route (excluding start + final
    // destination). Kept so a reroute (off-route or skip-waypoint) rebuilds
    // the route through the remaining waypoints instead of dropping them.
    private List<Models.Valhalla.Location> _viaLocations = [];

    // ── Session state ────────────────────────────────────────────────────
    private double _distanceToNextTurnM;
    private double _remainingDistanceKm;
    private double _remainingTimeMin;

    // Smoothing / route-matching state
    private Coordinate? _lastSmoothedPosition;
    private readonly List<Coordinate> _gpsBuffer = [];
    private int _gpsTickCount;
    private bool _verifyInFlight;

    private const int MapMatchBufferLimit = 5;
    private const int MapMatchTickInterval = 3;
    private const double GpsSmoothingAlpha = 0.7; // EMA: newest reading gets 70% weight (less lag)

    // Off-Route Detection
    // Standard threshold for straight road segments.
    // Maneuver threshold is higher because GPS points during turns
    // deviate 30-40m from the route polyline (curve vs straight segments).
    private const double OffRouteThresholdM = 25.0;
    private const double OffRouteManeuverThresholdM = 50.0;
    private const int OffRouteHysteresisCount = 2; // 2 consecutive points
    private int _offRouteCounter;
    private bool _isOffRoute;

    // Current travel direction (0-359°, 0 = north, clockwise). Updated on
    // every GPS tick: GPS course if valid, otherwise derived from the
    // smoothed position buffer. Used for reroute requests (heading +
    // heading_tolerance) so Valhalla starts the new route in the rider's
    // actual travel direction instead of picking an arbitrary first edge.
    private double _currentHeadingDeg = -1;
    private const double RerouteHeadingToleranceDeg = 45.0;

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

            // If the current maneuver is NOT 'straight', show it.
            if (!IsStraightManeuver(_maneuvers[_currentManeuverIndex]))
            {
                return _currentManeuverIndex;
            }

            // If it is 'straight', find the next relevant (non-straight) maneuver.
            foreach (int candidateIndex in _relevantManeuverIndices.Where(candidateIndex => candidateIndex > _currentManeuverIndex))
            {
                return candidateIndex;
            }

            // Fallback: if nothing is left, show the last maneuver
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
    /// <param name="mergedShape">Encoded polyline of the full route (all legs).</param>
    /// <param name="maneuvers">Maneuvers of the full route.</param>
    /// <param name="totalDistanceKm">Total route distance.</param>
    /// <param name="totalTimeMin">Total route duration.</param>
    /// <param name="destination">Final destination location.</param>
    /// <param name="viaLocations">Intermediate waypoints (without start/destination).
    /// Kept so a reroute keeps driving through the remaining waypoints.</param>
    public async Task StartNavigation(
        string mergedShape,
        List<Maneuver> maneuvers,
        double totalDistanceKm,
        double totalTimeMin,
        Models.Valhalla.Location destination,
        IReadOnlyList<Models.Valhalla.Location>? viaLocations = null)
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

            // Diagnostic: maneuver map (shape indices + cumulative distance)
            // so the exported log shows where each turn actually is. Resolves
            // "instruction stuck" vs. "turn is further along the route".
            Log.Information("MANEUVER MAP: {Points} pts, {Km:F2} km", _routeCoords.Count, totalDistanceKm);
            for (int i = 0; i < _maneuvers.Count; i++)
            {
                Maneuver m = _maneuvers[i];
                double len = _cumulativeDistances.Length > m.EndShapeIndex
                    ? _cumulativeDistances[m.EndShapeIndex] - _cumulativeDistances[m.BeginShapeIndex]
                    : 0;
                Log.Information("  M{Idx}: type={Type} shape[{Begin}..{End}] len={Len:F0}m {Instr}",
                    i + 1, m.Type, m.BeginShapeIndex, m.EndShapeIndex, len, m.Instruction);
            }

            _destination = destination;
            _viaLocations = viaLocations is { Count: > 0 } ? [.. viaLocations] : [];
            _isNavigating = true;
            _offRouteCounter = 0;
            
            
            _currentHeadingDeg = -1;
            _lastSmoothedPosition = null;
            _gpsTickCount = 0;
            _verifyInFlight = false;
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
            ManeuverCount = maneuvers.Count,
            // Real start coordinates for the DEST frame on the bike display.
            StartLatitude = _routeCoords.Count > 0 ? _routeCoords[0].Latitude : null,
            StartLongitude = _routeCoords.Count > 0 ? _routeCoords[0].Longitude : null
        }));

        await gps.StartTrackingAsync(backgroundMode: true);

        // Guard: navigation may have been stopped while GPS was starting.
        bool stillNavigating;
        lock (_lock)
        {
            stillNavigating = _isNavigating;
        }
        if (!stillNavigating)
        {
            await gps.StopTrackingAsync();
            return;
        }

        // Unsubscribe first to prevent duplicate subscriptions on restarts.
        gps.ReadingReceived -= OnGpsReading;
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
            _viaLocations = [];
            _offRouteCounter = 0;
            
            
            _currentHeadingDeg = -1;
            _verifyInFlight = false;
        }

        if (wasNavigating)
        {
            await gps.StopTrackingAsync();
            Log.Information("Navigation cancelled by user");
            _ = NotifySinksAsync(s => s.OnCancelAsync());
        }
    }

    /// <summary>
    /// Performs a reroute calculation from the current position to the destination,
    /// keeping the remaining waypoints of the active route.
    /// </summary>
    /// <param name="currentLat">Current latitude.</param>
    /// <param name="currentLon">Current longitude.</param>
    /// <param name="skipNextWaypoint">
    /// When true, the first not-yet-reached waypoint is skipped (user pressed
    /// "skip waypoint"). The reroute then goes through all remaining waypoints.
    /// </param>
    /// <returns>
    /// A tuple: <c>Success</c> whether the reroute was calculated, and
    /// <c>SkippedViaIndex</c> – when skipNextWaypoint skipped a waypoint, the
    /// index of the skipped waypoint in the via-locations list passed to
    /// StartNavigation; otherwise -1. Lets the caller remove the waypoint pin.
    /// </returns>
    public async Task<(bool Success, int SkippedViaIndex)> PerformRerouteAsync(
        double currentLat, double currentLon, bool skipNextWaypoint)
    {
        int skippedViaIndex = -1;
        if (!_isNavigating || _destination == null) return (false, -1);

        try
        {
            // Current progress along the old route – used to drop waypoints
            // the rider has already passed.
            int snappedIndex = _routeCoords.Count > 0
                ? FindNearestRouteIndex(currentLat, currentLon).Index
                : 0;

            // 1. Build Request – start + remaining waypoints + destination.
            // The waypoints must be kept, otherwise Valhalla routes directly
            // to the final destination and silently drops all intermediate
            // stops ("skip waypoint" used to skip ALL waypoints).
            List<Models.Valhalla.Location> locs =
            [
                new()
                {
                    Lat = currentLat,
                    Lon = currentLon,
                    Type = "break",
                    // Start the new route in the rider's actual travel
                    // direction so Valhalla does not pick an arbitrary first
                    // edge (e.g. a 180° turnaround after a missed turn).
                    Heading = _currentHeadingDeg > 0 ? _currentHeadingDeg : null,
                    HeadingTolerance = RerouteHeadingToleranceDeg
                }
            ];

            // Remaining waypoints: drop every one the rider has already
            // passed (its nearest route index is behind the current snapped
            // index), so the reroute only goes through waypoints still ahead.
            List<Models.Valhalla.Location> remaining = _viaLocations
                .Where(v => FindNearestRouteIndex(v.Lat, v.Lon).Index >= snappedIndex - 1)
                .ToList();

            if (skipNextWaypoint && remaining.Count > 0)
            {
                // Report which waypoint was skipped (index in the original
                // via list) so the caller can drop the matching pin.
                skippedViaIndex = _viaLocations.IndexOf(remaining[0]);
                remaining = remaining.Skip(1).ToList();
            }

            locs.AddRange(remaining.Select((t, i) => new Models.Valhalla.Location { Lat = t.Lat, Lon = t.Lon, Type = i == 0 ? "break" : "via" }));

            locs.Add(_destination);

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
                return (false, -1);
            }

            RouteResponse response = result.Response;

            // 3. Update state – the remaining waypoints are the new via set
            // (the skipped one is gone).
            (string mergedShape, List<Maneuver> maneuvers, double totalKm, double totalMin) =
                PrepareNavigationData(response.Trip?.Legs ?? []);
            if (string.IsNullOrEmpty(mergedShape)) return (false, -1);

            lock (_lock)
            {
                Reroute(mergedShape, maneuvers, totalKm, totalMin);
                _viaLocations = remaining;
            }

            // 4. Notify sinks
            _ = NotifySinksAsync(s => s.OnRouteUpdatedAsync(response));

            Log.Information("Reroute successful ({ViaCount} waypoints remaining).", remaining.Count);
            return (true, skippedViaIndex);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reroute calculation error");
            return (false, -1);
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
            
            
            _currentHeadingDeg = -1;
            _lastSmoothedPosition = null;
            _verifyInFlight = false;
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
        List<Coordinate> rawCoords = PolylineEncoder.DecodePolyline6(mergedShape);

        // Build index map: for each raw index, what's the densified index?
        // Needed because densification inserts points between originals,
        // shifting maneuver shape indices.
        List<int> rawToDensified = [0];
        List<Coordinate> densified = [rawCoords[0]];
        const double maxSegM = 20.0;
        for (int i = 1; i < rawCoords.Count; i++)
        {
            Coordinate a = rawCoords[i - 1];
            Coordinate b = rawCoords[i];
            double dist = GeoMath.DistanceMeters(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
            if (dist > maxSegM)
            {
                int n = (int)Math.Ceiling(dist / maxSegM);
                for (int j = 1; j < n; j++)
                {
                    double t = (double)j / n;
                    densified.Add(new Coordinate(
                        a.Latitude + t * (b.Latitude - a.Latitude),
                        a.Longitude + t * (b.Longitude - a.Longitude), null));
                }
            }
            densified.Add(b);
            rawToDensified.Add(densified.Count - 1);
        }
        _routeCoords = densified;

        // Remap maneuver shape indices to densified polyline
        foreach (Maneuver m in maneuvers)
        {
            if (m.BeginShapeIndex < rawToDensified.Count)
                m.BeginShapeIndex = rawToDensified[m.BeginShapeIndex];
            if (m.EndShapeIndex < rawToDensified.Count)
                m.EndShapeIndex = rawToDensified[m.EndShapeIndex];
        }
        _maneuvers = maneuvers;
        UpdateCumulativeDistances();
        // Cache the threshold once per route instead of reading Preferences per GPS tick.
        // Threshold is now a constant, no longer user-configurable;

        // 1. Precomputed list of indices of relevant maneuvers (non-straight)
        _relevantManeuverIndices = maneuvers
            .Select((m, i) => (m, i))
            .Where(x => !IsStraightManeuver(x.m))
            .Select(x => x.i)
            .ToList();

        // 2. Append the indices of all straight maneuvers at the end
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

            // Off-Route Detection
            //
            // Two thresholds: 25m on straight segments, 50m during maneuvers.
            // Turns cause 30-40m GPS deviation from route polyline (curve vs
            // straight segments). Without the higher maneuver threshold,
            // every left/right turn triggers false off-route.
            // ───────────────────────────────────────────────────────────────

            // EMA smoothing
            double smoothLat = reading.Position.Latitude;
            double smoothLon = reading.Position.Longitude;
            if (_lastSmoothedPosition != null)
            {
                smoothLat = GpsSmoothingAlpha * reading.Position.Latitude + (1.0 - GpsSmoothingAlpha) * _lastSmoothedPosition.Value.Latitude;
                smoothLon = GpsSmoothingAlpha * reading.Position.Longitude + (1.0 - GpsSmoothingAlpha) * _lastSmoothedPosition.Value.Longitude;
            }
            _lastSmoothedPosition = new Coordinate(smoothLat, smoothLon, null);

            _gpsTickCount++;
            _gpsBuffer.Add(new Coordinate(smoothLat, smoothLon, null));
            if (_gpsBuffer.Count > MapMatchBufferLimit) _gpsBuffer.RemoveAt(0);

            // Track travel direction
            if (reading.Heading > 0)
                _currentHeadingDeg = reading.Heading;
            else
            {
                double? bufferHeading = BearingFromBuffer();
                if (bufferHeading is { } bh)
                    _currentHeadingDeg = bh;
            }

            double navLat = smoothLat;
            double navLon = smoothLon;

            // Snap to route
            (int snappedIndex, double distanceMeters) = FindNearestRouteIndex(navLat, navLon);
            if (snappedIndex < 0) return;

            // Determine if we're inside a maneuver span (turning).
            // During turns, GPS deviates 30-40m from the route polyline.
            bool inManeuverSpan = false;
            foreach (Maneuver m in _maneuvers)
            {
                if (snappedIndex >= m.BeginShapeIndex && snappedIndex <= m.EndShapeIndex)
                {
                    inManeuverSpan = true;
                    break;
                }
            }
            double effectiveThreshold = inManeuverSpan ? OffRouteManeuverThresholdM : OffRouteThresholdM;

            // Off-route check
            if (distanceMeters > effectiveThreshold)
            {
                _offRouteCounter++;
                if (_offRouteCounter >= OffRouteHysteresisCount && !_isOffRoute)
                {
                    _isOffRoute = true;
                    Log.Warning("Off route! {Dist:F1}m from route, threshold={Threshold:F1}m, maneuver={Man}",
                        distanceMeters, effectiveThreshold, inManeuverSpan);
                    _ = NotifySinksAsync(s => s.OnOffRouteAsync(navLat, navLon, distanceMeters));
                }
                if (_isOffRoute) return;
            }
            else
            {
                _offRouteCounter = 0;
            }

            // Back on route
            if (!_isOffRoute || distanceMeters <= effectiveThreshold)
            {
                _isOffRoute = false;
                _offRouteCounter = 0;
            }

            // Periodic Off-Route Verification via API (one in-flight at a time)
            if (_gpsTickCount % MapMatchTickInterval == 0 && !_verifyInFlight)
            {
                _verifyInFlight = true;
                _ = VerifyRouteAsync();
            }


            // Upcoming action: first maneuver whose begin is at/ahead of the
            // snap. Valhalla maneuvers describe the action at their BEGIN
            // (the turn happens at begin_shape_index, e.g. "Turn left onto
            // Rotebühlstraße" at shape 3, spanning the whole road segment
            // after it). Picking the span-containing maneuver would keep a
            // finished turn's instruction on screen for the entire following
            // segment - the stuck-instruction bug. Show the NEXT action.
            int newManeuverIndex = -1;
            if (_maneuvers.Count > 0)
            {
                if (snappedIndex >= _maneuvers[^1].EndShapeIndex)
                {
                    newManeuverIndex = -1; // destination reached
                }
                else
                {
                    newManeuverIndex = _maneuvers.Count - 1;
                    for (int i = 0; i < _maneuvers.Count; i++)
                    {
                        if (_maneuvers[i].BeginShapeIndex >= snappedIndex)
                        {
                            newManeuverIndex = i;
                            break;
                        }
                    }
                }
            }

            // Arrival: the snapped index is past the last maneuver's end
            // (Valhalla "Arrive" has begin == end == last shape index, which
            // never satisfies the routeIndex < end condition). Treat -1 as
            // destination reached instead of corrupting the maneuver index.
            if (newManeuverIndex < 0 && _maneuvers.Count > 0)
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

            int displayIndex = GetDisplayManeuverIndex();

            bool maneuverChanged = newManeuverIndex != _currentManeuverIndex;
            // Never regress: GPS jitter near a corner (snap bounces between
            // two maneuvers) must not move the instruction backwards.
            if (newManeuverIndex < _currentManeuverIndex)
                maneuverChanged = false;

            if (maneuverChanged)
            {
                _currentManeuverIndex = newManeuverIndex;
                displayIndex = GetDisplayManeuverIndex();

                FireCurrentManeuver(displayIndex);
            }

            // Distance to the DISPLAYED action (skip-straight can point past
            // _currentManeuverIndex, e.g. the depart maneuver at start).
            _distanceToNextTurnM = CalculateDistanceToNextTurn(snappedIndex, displayIndex);
            (double remainingKm, double remainingMin) = CalculateRemaining(snappedIndex);
            _remainingDistanceKm = remainingKm;
            _remainingTimeMin = remainingMin;

            // Diagnostic trace on EVERY reading: with sparse GPS fixes (e.g.
            // simulator, few points) a %10 throttle never fires, so the log
            // would show nothing. One line per reading is fine (1 Hz).
            Log.Information(
                "NAV tick {Tick}: pos=({Lat:F6},{Lon:F6}) snap={Snap} man={Man}/{Total} distTurn={Dist:F0}m rem={Rem:F1}km offRoute={OffRoute}",
                _gpsTickCount, navLat, navLon, snappedIndex,
                _currentManeuverIndex, _maneuvers.Count,
                _distanceToNextTurnM, _remainingDistanceKm, _isOffRoute);

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
                // Computed travel direction (GPS course or buffer-derived),
                // not the raw GPS course which is often 0/unset.
                Heading = _currentHeadingDeg > 0 ? _currentHeadingDeg : reading.Heading,
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

    /// <summary>
    /// Travel direction derived from the last two smoothed GPS fixes that are
    /// far enough apart (initial bearing a→b). Returns null when the rider is
    /// (nearly) standing or the buffer has too few points.
    /// </summary>
    private double? BearingFromBuffer()
    {
        if (_gpsBuffer.Count < 2) return null;

        for (int i = _gpsBuffer.Count - 1; i >= 1; i--)
        {
            Coordinate a = _gpsBuffer[i - 1];
            Coordinate b = _gpsBuffer[i];
            double distM = GeoMath.DistanceMeters(
                a.Latitude, a.Longitude, b.Latitude, b.Longitude);
            if (distM < 10.0)
                continue;

            // Initial bearing (great-circle) from a to b = movement direction.
            double phi1 = GeoMath.ToRad(a.Latitude);
            double phi2 = GeoMath.ToRad(b.Latitude);
            double dLon = GeoMath.ToRad(b.Longitude - a.Longitude);
            double y = Math.Sin(dLon) * Math.Cos(phi2);
            double x = Math.Cos(phi1) * Math.Sin(phi2) -
                       Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLon);
            double bearingDeg = (GeoMath.ToDeg(Math.Atan2(y, x)) + 360.0) % 360.0;
            return bearingDeg;
        }
        return null;
    }

    private (int Index, double DistanceMeters) FindNearestRouteIndex(
        double lat, double lon)
    {
        if (_routeCoords.Count == 0)
            return (-1, double.MaxValue);

        int bestIndex = 0;
        double bestDistSq = double.MaxValue;

        // Full-route scan: the old +-20 hint window pinned the snap to the
        // window edge on GPS jumps (curve taken between two sparse fixes),
        // so the maneuver never advanced past the turn. A few hundred route
        // points at 1 Hz is trivial to scan completely.
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


    // TODO: integrate Valhalla map‑matching confidence weighting
    private double CalculateDistanceToNextTurn(int currentRouteIndex, int displayIndex)
    {
        if (displayIndex < 0 || displayIndex >= _maneuvers.Count) return 0;


        // Distance to the DISPLAYED action = that maneuver's begin (the
        // turn happens at begin_shape_index), clamped against overshoot.
        int targetIndex = _maneuvers[displayIndex].BeginShapeIndex;
        if (targetIndex >= _routeCoords.Count)
            targetIndex = _routeCoords.Count - 1;

        if (currentRouteIndex >= _cumulativeDistances.Length - 1) return 0;
        return Math.Max(0, _cumulativeDistances[targetIndex] - _cumulativeDistances[currentRouteIndex]);
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
        try
        {
            List<Coordinate> bufferSnapshot;
            lock (_lock)
            {
                if (!_isNavigating) return;
                bufferSnapshot = _gpsBuffer.ToList();
            }

            if (bufferSnapshot.Count < 2) return;

            TraceRequest request = new()
            {
                Shape = bufferSnapshot.Select(p => new ShapePoint(p.Longitude, p.Latitude)).ToList(),
                Costing = "motorcycle",
                TraceOptions = new TraceOptions { SearchRadius = 50 }
            };
            // shape_match=map_snap set by ValhallaClient.GetMapMatchAsync

            Result result = await valhallaClient.GetMapMatchAsync(request);

            if (!result.IsSuccess || result.Response == null) return;

            RouteResponse response = result.Response;
            Trip? snappedTrip = response.Trip;
            if (snappedTrip?.Legs == null || snappedTrip.Legs.Count == 0) return;

            Coordinate lastSnapped = PolylineEncoder.DecodePolyline6(snappedTrip.Legs[0].Shape).Last();

            (int _, double distToRoute) = FindNearestRouteIndex(
                lastSnapped.Latitude, lastSnapped.Longitude);

            lock (_lock)
            {
                if (!_isNavigating) return;

                // Map-matching verification: only used to reset off-route state
                // when the rider is confirmed back on the route. NOT used as
                // off-route trigger (map-matching polyline distance is unreliable
                // — 17-75m deviation even when on-route).
                if (distToRoute <= OffRouteThresholdM)
                {
                    if (_isOffRoute)
                    {
                        _isOffRoute = false;
                        _offRouteCounter = 0;
                        Log.Information("Back on route (map-match verified)");
                    }
                }
                Log.Debug("Map-matched position: {Lat}, {Lon} (verify only, not used for nav)", lastSnapped.Latitude, lastSnapped.Longitude);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during route verification");
        }
        finally
        {
            lock (_lock)
            {
                _verifyInFlight = false;
            }
        }
    }

    // ── Maneuver change notification ─────────────────────────────────────
    // Note: map‑matching could be used here to refine upcoming maneuver info
    private void FireCurrentManeuver(int index = -1)
    {
        int targetIndex = index != -1 ? index : GetDisplayManeuverIndex();
        
        if (targetIndex < 0 || targetIndex >= _maneuvers.Count) return;

        Maneuver m = _maneuvers[targetIndex];

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

        Log.Information(
            "MANEUVER {I}/{T}: {Instr} (ValhallaType={Type})",
            targetIndex + 1, _maneuvers.Count,
            m.Instruction, m.Type);

        _ = NotifySinksAsync(s => s.OnManeuverAsync(info));
    }
}
