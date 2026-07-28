using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using VegaBridgeApp.Models.Valhalla;
using VegaBridgeApp.Services.Location;
using VegaBridgeApp.Services.Routes;
using VegaBridgeApp.Utils;
using Shiny.Locations;
using Coordinate = VegaBridgeApp.Models.Valhalla.Coordinate;

namespace VegaBridgeApp.Services.Navigation;

/// <summary>
/// Navigation state machine.
/// 
/// Tracks the rider's position along a Valhalla route, determines the current
/// maneuver, calculates distances, and fires events for the UI and BLE layers.
/// 
/// Works with the screen OFF – the UI is only needed for optional glanceable
/// updates. The core logic runs from GPS callbacks regardless of display state.
/// </summary>
public class NavigationService
{
    private readonly GpsService _gps;
    private readonly ILogger<NavigationService> _logger;
    private readonly object _lock = new();

    // ── Route data (set once per navigation session) ──────────────────────
    private List<Coordinate> _routeCoords = [];
    private List<Maneuver> _maneuvers = [];
    private int _currentManeuverIndex;
    private bool _isNavigating;

    // ── Session state ────────────────────────────────────────────────────
    private double _totalDistanceKm;
    private double _totalTimeMin;
    private GpsReading? _lastReading;
    private double _distanceToNextTurnM;
    private double _remainingDistanceKm;
    private double _remainingTimeMin;
    private bool _isOffRoute;

    private const double OffRouteThresholdDefaultM = 10.0;

    private double OffRouteThresholdM => Preferences.Get("off_route_threshold", OffRouteThresholdDefaultM);

    public NavigationService(GpsService gps, ILogger<NavigationService> logger)
    {
        _gps = gps;
        _logger = logger;
    }

    // ── Events (for UI + BLE layers) ─────────────────────────────────────

    /// <summary>Fired when the rider enters a new maneuver zone.</summary>
    public event Action<NavigationManeuverInfo>? ManeuverChanged;

    /// <summary>Fired periodically (~1 Hz) with current speed / distance / ETA.</summary>
    public event Action<NavigationStatus>? StatusUpdated;

    /// <summary>Fired when the destination is reached.</summary>
    public event Action? NavigationCompleted;

    /// <summary>Fired when navigation starts or stops.</summary>
    public event Action<bool>? NavigationStateChanged;

    /// <summary>Fired when the rider is significantly off the planned route.</summary>
    public event Action<double, double, double>? OffRouteDetected; // lat, lon, distanceM

    /// <summary>Fired for every BLE command that would be sent (simulation mode).</summary>
    public event Action<string, byte[]>? BleCommandSimulated;

    // ── Properties ───────────────────────────────────────────────────────

    public bool IsNavigating => _isNavigating;
    public int CurrentManeuverIndex => _currentManeuverIndex;
    public int TotalManeuvers => _maneuvers.Count;
    public Maneuver? CurrentManeuver =>
        _maneuvers.Count > _currentManeuverIndex ? _maneuvers[_currentManeuverIndex] : null;
    public double TotalDistanceKm => _totalDistanceKm;
    public double TotalTimeMin => _totalTimeMin;
    public bool IsOffRoute => _isOffRoute;

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Start navigating along a Valhalla route.
    /// </summary>
    /// <param name="mergedShape">Polyline6 of the complete route (all legs merged).</param>
    /// <param name="maneuvers">All maneuvers across all legs.</param>
    /// <param name="totalDistanceKm">Total route distance.</param>
    /// <param name="totalTimeMin">Total estimated time.</param>
    public void StartNavigation(
        string mergedShape,
        List<Maneuver> maneuvers,
        double totalDistanceKm,
        double totalTimeMin)
    {
        lock (_lock)
        {
            if (_isNavigating) StopNavigation();

            _routeCoords = PolylineEncoder.DecodePolyline6(mergedShape);
            _maneuvers = maneuvers;
            _totalDistanceKm = totalDistanceKm;
            _totalTimeMin = totalTimeMin;
            _currentManeuverIndex = 0;
            _remainingDistanceKm = totalDistanceKm;
            _remainingTimeMin = totalTimeMin;

            _isNavigating = true;
        }

        NavigationStateChanged?.Invoke(true);

        // Subscribe to GPS updates
        _gps.ReadingReceived += OnGpsReading;

        _logger.LogInformation(
            "Navigation started: {Distance:F1} km, {Time:F0} min, {Maneuvers} maneuvers, {Points} route points",
            totalDistanceKm, totalTimeMin, maneuvers.Count, _routeCoords.Count);

        // Fire initial state
        FireCurrentManeuver();
    }

    /// <summary>Stop the current navigation session.</summary>
    public void StopNavigation()
    {
        lock (_lock)
        {
            if (!_isNavigating) return;

            _gps.ReadingReceived -= OnGpsReading;
            _isNavigating = false;
            _currentManeuverIndex = 0;
            _routeCoords = [];
            _maneuvers = [];
        }

        NavigationStateChanged?.Invoke(false);
        _logger.LogInformation("Navigation stopped");
    }

    /// <summary>
    /// Replace the route mid-navigation after a reroute.
    /// Call this from the UI after getting a new route from Valhalla.
    /// </summary>
    public void Reroute(string mergedShape, List<Maneuver> maneuvers, double totalDistanceKm, double totalTimeMin)
    {
        lock (_lock)
        {
            _routeCoords = PolylineEncoder.DecodePolyline6(mergedShape);
            _maneuvers = maneuvers;
            _totalDistanceKm = totalDistanceKm;
            _totalTimeMin = totalTimeMin;
            _currentManeuverIndex = 0;
            _remainingDistanceKm = totalDistanceKm;
            _remainingTimeMin = totalTimeMin;
            _isOffRoute = false;
        }

        FireCurrentManeuver();
        _logger.LogInformation(
            "Route rerouted: {Distance:F1} km, {Time:F0} min, {Maneuvers} maneuvers",
            totalDistanceKm, totalTimeMin, maneuvers.Count);
    }

    // ── GPS event handler (core loop) ────────────────────────────────────

    private void OnGpsReading(GpsReading reading)
    {
        lock (_lock)
        {
            if (!_isNavigating || _routeCoords.Count < 2) return;

        _lastReading = reading;

        // 1. Snap current position to route + check off-route
        (int snappedIndex, double distanceMeters) = FindNearestRouteIndex(
            reading.Position.Latitude, reading.Position.Longitude);

        if (snappedIndex < 0) return;

        // Off-route detection
        if (distanceMeters > OffRouteThresholdM)
        {
            if (!_isOffRoute)
            {
                _isOffRoute = true;
                _logger.LogWarning(
                    "Off route! {Dist:F1}m from route", distanceMeters);
                OffRouteDetected?.Invoke(
                    reading.Position.Latitude, reading.Position.Longitude, distanceMeters);
            }
            // Don't update maneuver/distances while off-route
            return;
        }

        // Back on route
        _isOffRoute = false;
        int newManeuverIndex = FindManeuverForShapeIndex(snappedIndex);
        bool maneuverChanged = newManeuverIndex != _currentManeuverIndex;

        if (maneuverChanged)
        {
            _currentManeuverIndex = newManeuverIndex;

            // Check for arrival
            if (_currentManeuverIndex >= _maneuvers.Count)
            {
                _logger.LogInformation("Destination reached!");
                NavigationCompleted?.Invoke();
                StopNavigation();
                return;
            }

            FireCurrentManeuver();
        }

        // 3. Calculate distances
        _distanceToNextTurnM = CalculateDistanceToNextTurn(snappedIndex);
        (double remainingKm, double remainingMin) = CalculateRemaining(snappedIndex);
        _remainingDistanceKm = remainingKm;
        _remainingTimeMin = remainingMin;

        // 4. Fire status update
        double speedKmh = reading.Speed * 3.6;
        NavigationStatus status = new()
        {
            SpeedKmh = speedKmh,
            DistanceToNextTurnM = _distanceToNextTurnM,
            RemainingDistanceKm = _remainingDistanceKm,
            RemainingTimeMin = _remainingTimeMin,
            CurrentManeuverIndex = _currentManeuverIndex,
            TotalManeuvers = _maneuvers.Count,
            Heading = reading.Heading,
            Accuracy = reading.PositionAccuracy,
            IsStationary = reading.IsStationary
        };
        StatusUpdated?.Invoke(status);

        // 5. Simulate BLE commands (logged, not sent yet)
        SimulateBleCommands(status, maneuverChanged);
        }
    }

    // ── Route matching ───────────────────────────────────────────────────

    /// <summary>
    /// Find the nearest point on the route polyline to the given lat/lon.
    /// Returns the index into <see cref="_routeCoords"/> and the distance in meters.
    /// </summary>
    private (int Index, double DistanceMeters) FindNearestRouteIndex(double lat, double lon)
    {
        int bestIndex = 0;
        double bestDistSq = double.MaxValue;

        for (int i = 0; i < _routeCoords.Count - 1; i++)
        {
            double distSq = PointToSegmentDistanceSq(
                lon, lat,
                _routeCoords[i].Longitude, _routeCoords[i].Latitude,
                _routeCoords[i + 1].Longitude, _routeCoords[i + 1].Latitude,
                out double t);

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestIndex = t >= 0.5 ? i + 1 : i;
            }
        }

        double distanceMeters = GeoMath.DistanceMeters(
            lat, lon,
            _routeCoords[bestIndex].Latitude, _routeCoords[bestIndex].Longitude);
        return (bestIndex, distanceMeters);
    }

    /// <summary>Squared distance from point (px,py) to segment (ax,ay)-(bx,by).</summary>
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

        return _maneuvers.Count;
    }

    private double CalculateDistanceToNextTurn(int currentRouteIndex)
    {
        if (_currentManeuverIndex >= _maneuvers.Count) return 0;

        int targetIndex = _maneuvers[_currentManeuverIndex].EndShapeIndex;
        if (targetIndex >= _routeCoords.Count)
            targetIndex = _routeCoords.Count - 1;

        double totalM = 0;
        for (int i = currentRouteIndex; i < targetIndex && i < _routeCoords.Count - 1; i++)
        {
            totalM += GeoMath.DistanceMeters(
                _routeCoords[i].Latitude, _routeCoords[i].Longitude,
                _routeCoords[i + 1].Latitude, _routeCoords[i + 1].Longitude);
        }

        return totalM;
    }

    private (double RemainingKm, double RemainingMin) CalculateRemaining(int currentRouteIndex)
    {
        if (currentRouteIndex >= _routeCoords.Count - 1)
            return (0, 0);

        double remainingM = 0;
        for (int i = currentRouteIndex; i < _routeCoords.Count - 1; i++)
        {
            remainingM += GeoMath.DistanceMeters(
                _routeCoords[i].Latitude, _routeCoords[i].Longitude,
                _routeCoords[i + 1].Latitude, _routeCoords[i + 1].Longitude);
        }

        double remainingKm = remainingM / 1000.0;
        double fractionRemaining = _totalDistanceKm > 0
            ? remainingKm / _totalDistanceKm
            : 1;
        double remainingMin = _totalTimeMin * fractionRemaining;

        return (remainingKm, remainingMin);
    }

    // ── Maneuver change notification ─────────────────────────────────────

    private void FireCurrentManeuver()
    {
        Maneuver? m = CurrentManeuver;
        if (m == null) return;

        NavigationManeuverInfo info = new()
        {
            Index = _currentManeuverIndex,
            Total = _maneuvers.Count,
            Instruction = m.Instruction ?? "",
            StreetNames = m.StreetNames ?? [],
            LengthKm = m.Length,
            TimeMin = m.Time / 60.0,
            TurnDegree = m.TurnDegree,
            RoundaboutExitCount = m.RoundaboutExitCount,
            TravelMode = m.TravelMode,
            TravelType = m.TravelType,
            BLEIcon = MapValhallaToMvAgusta(m.Type),
            RoundaboutExit = m.RoundaboutExit
        };

        ManeuverChanged?.Invoke(info);
        _logger.LogDebug(
            "Maneuver {I}/{T}: {Instr} ({Icon})",
            _currentManeuverIndex + 1, _maneuvers.Count,
            m.Instruction, info.BLEIcon);
    }

    // ── BLE simulation ──────────────────────────────────────────────────

    private void SimulateBleCommands(NavigationStatus status, bool maneuverChanged)
    {
        if (maneuverChanged && CurrentManeuver != null)
        {
            string icon = MapValhallaToMvAgusta(CurrentManeuver.Type);
            string instruction = CurrentManeuver.Instruction ?? "";
            string street = CurrentManeuver.StreetNames?.FirstOrDefault() ?? "";

            byte[] naviFrame = BuildBleFrame("NAVI", icon, instruction, street);
            BleCommandSimulated?.Invoke("NAVI", naviFrame);

            byte[] smFrame = BuildBleFrame("SM",
                status.SpeedKmh.ToString("F0"),
                (status.RemainingDistanceKm * 1000).ToString("F0"),
                status.DistanceToNextTurnM.ToString("F0"));
            BleCommandSimulated?.Invoke("SM", smFrame);
        }

        byte[] smUpdate = BuildBleFrame("SM",
            status.SpeedKmh.ToString("F0"),
            (status.RemainingDistanceKm * 1000).ToString("F0"),
            status.DistanceToNextTurnM.ToString("F0"));
        BleCommandSimulated?.Invoke("SM (periodic)", smUpdate);
    }

    private static byte[] BuildBleFrame(params string[] fields)
    {
        string frame = $"\r{string.Join("\x1E", fields)}\r";
        return System.Text.Encoding.UTF8.GetBytes(frame);
    }

    // ── Valhalla → MV Agusta turn type mapping ───────────────────────────

    private static string MapValhallaToMvAgusta(int valhallaType)
    {
        return valhallaType switch
        {
            1 => "turn-right",
            2 => "turn-left",
            3 => "straight",
            4 => "turn-slight-right",
            5 => "turn-slight-left",
            6 => "turn-slight-right",
            7 => "turn-slight-left",
            8 => "straight",
            9 => "turn-slight-right",
            10 => "turn-slight-left",
            11 => "straight",
            12 => "straight",
            13 => "roundabout-right-1",
            14 => "roundabout-left-1",
            15 => "Finish",
            16 => "Finish",
            _ => "straight"
        };
    }
}

// ── Event payloads ─────────────────────────────────────────────────────

public class NavigationManeuverInfo
{
    public int Index { get; init; }
    public int Total { get; init; }
    public string Instruction { get; init; } = "";
    public List<string> StreetNames { get; init; } = [];
    public double LengthKm { get; init; }
    public double TimeMin { get; init; }
    public double? TurnDegree { get; init; }
    public int? RoundaboutExitCount { get; init; }
    public string? TravelMode { get; init; }
    public string? TravelType { get; init; }
    public string BLEIcon { get; init; } = "straight";
    public int? RoundaboutExit { get; init; }
}

public class NavigationStatus
{
    public double SpeedKmh { get; init; }
    public double DistanceToNextTurnM { get; init; }
    public double RemainingDistanceKm { get; init; }
    public double RemainingTimeMin { get; init; }
    public int CurrentManeuverIndex { get; init; }
    public int TotalManeuvers { get; init; }
    public double Heading { get; init; }
    public double Accuracy { get; init; }
    public bool IsStationary { get; init; }
}
