using Serilog;
using VegaBridgeApp.Models.BLE;
using VegaBridgeApp.Models.Valhalla;
using VegaBridgeApp.Services.Navigation;
using VegaBridgeApp.Models.Navigation;

namespace VegaBridgeApp.Services.BLE;

/// <summary>
/// Mediator bridging NavigationService (domain) → BleManagerService (transport).
///
/// Listens to navigation events, enriches them with semantic data, and delegates
/// plugin calls to the BleManagerService (which owns the active plugin and device).
/// </summary>
public class BleNavigationCoordinator : IDisposable
{
    private readonly NavigationService _navigation;
    private readonly BleManagerService _bleManager;

    // Throttling for periodic status updates (SM frames)
    private DateTimeOffset _lastStatusSent = DateTimeOffset.MinValue;
    private readonly TimeSpan _statusThrottleInterval = TimeSpan.FromMilliseconds(500);

    // Current context
    private NavigationManeuverInfo? _currentManeuver;
    private NavigationStatus? _currentStatus;
    private bool _isNavigating;

    public BleNavigationCoordinator(
        NavigationService navigation,
        BleManagerService bleManager)
    {
        _navigation = navigation;
        _bleManager = bleManager;

        Log.Information("BleNavigationCoordinator initializing and subscribing to events...");
        
        _navigation.NavigationStateChanged += OnNavigationStateChanged;
        _navigation.ManeuverChanged += OnManeuverChanged;
        _navigation.StatusUpdated += OnStatusUpdated;
        _navigation.OffRouteDetected += OnOffRouteDetected;
        _navigation.NavigationCompleted += OnNavigationCompleted;

        // Sync if already navigating
        if (_navigation.IsNavigating)
        {
            _isNavigating = true;
            _currentManeuver = GetManeuverInfo();
        }

        Log.Information("BleNavigationCoordinator is now active.");
    }

    public void Dispose()
    {
        _navigation.NavigationStateChanged -= OnNavigationStateChanged;
        _navigation.ManeuverChanged -= OnManeuverChanged;
        _navigation.StatusUpdated -= OnStatusUpdated;
        _navigation.OffRouteDetected -= OnOffRouteDetected;
        _navigation.NavigationCompleted -= OnNavigationCompleted;

        _isNavigating = false;
        _currentManeuver = null;
        _currentStatus = null;
    }

    // ─── Event Handlers ──────────────────────────────────────────────────

    private void OnNavigationStateChanged(bool isNavigating)
    {
        switch (isNavigating)
        {
            case true when !_isNavigating:
                _isNavigating = true;
                _currentManeuver = GetManeuverInfo();
                _currentStatus = null;
                _ = Task.Run(async () => await SendStartAsync());
                break;
            case false when _isNavigating:
                _isNavigating = false;
                _currentManeuver = null;
                _currentStatus = null;
                break;
        }
    }

    private void OnManeuverChanged(NavigationManeuverInfo info)
    {
        _currentManeuver = info;
        _ = Task.Run(async () => await SendUpdateAsync());
    }

    private void OnStatusUpdated(NavigationStatus status)
    {
        _currentStatus = status;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastStatusSent < _statusThrottleInterval)
            return;

        _lastStatusSent = now;
        _ = Task.Run(async () => await SendUpdateAsync());
    }

    private void OnOffRouteDetected(double lat, double lon, double distM)
    {
        OffRouteAlertInput input = new()
        {
            DistanceMeters = distM,
            Latitude = lat,
            Longitude = lon,
            DetectedAt = DateTimeOffset.UtcNow
        };

        _ = Task.Run(async () => await _bleManager.ExecuteNavigationActionAsync(
            nameof(IBleDevicePlugin.SendOffRouteAlertAsync),
            input));
    }

    private void OnNavigationCompleted()
    {
        _isNavigating = false;
        _currentManeuver = null;
        _currentStatus = null;

        _ = Task.Run(async () => await _bleManager.ExecuteNavigationFinishAsync());
    }

    // ─── Sending Helpers ─────────────────────────────────────────────────

    private async Task SendStartAsync()
    {
        if (_currentManeuver == null) return;

        // Ensure we send correct units and matching field structures:
        // DEST: Address, Lat, Lon (all 3 must be filled even as placeholders)
        // REM: Remaining distance in METERS (as integer string), not kilometers.
        NavigationStartInput input = new()
        {
            TotalDistanceKm = _navigation.TotalDistanceKm,
            TotalTimeMin = _navigation.TotalTimeMin,
            UpcomingManeuvers = []
        };

        BleCommandLogger.Log($"NAV START: distance={input.TotalDistanceKm:F1}km, time={input.TotalTimeMin:F0}min, maneuvers={_navigation.TotalManeuvers}");

        await _bleManager.ExecuteNavigationActionAsync(
            "SendNavigationStartAsync", input);
    }

    private async Task SendUpdateAsync()
    {
        if (!_isNavigating || _currentManeuver == null || _currentStatus == null)
            return;

        NavigationStatus status = _currentStatus;
        NavigationManeuverInfo maneuver = _currentManeuver;

        // The display maneuver (look-ahead) contains the upcoming turn's street names.
        // IntersectionName = road name of the upcoming maneuver (street you're turning ONTO).
        // This matches the official MV Ride app: direction.getRoadName() from HERE SDK.
        string intersectionName = maneuver.StreetNames.FirstOrDefault() ?? string.Empty;
        string street = intersectionName; // StreetName kept for compatibility; same value for MV Agusta

        NavigationUpdateInput input = new()
        {
            ManeuverIcon = NavigationIconMapper.GetSemanticIcon(maneuver.ValhallaType),
            InstructionText = maneuver.Instruction,
            StreetName = street,
            IntersectionName = intersectionName,
            DistanceToTurnM = status.DistanceToNextTurnM,
            SpeedKmh = status.SpeedKmh,
            RemainingDistanceKm = status.RemainingDistanceKm,
            RemainingTimeMin = status.RemainingTimeMin,
            CurrentManeuverIndex = maneuver.Index,
            TotalManeuvers = maneuver.Total,
            IsFinal = maneuver.Index >= maneuver.Total - 1 && status.DistanceToNextTurnM <= 0
        };

        BleCommandLogger.Log($"NAV UPDATE INPUT: icon={input.ManeuverIcon}, instr={input.InstructionText}, street={input.StreetName}, dist={input.DistanceToTurnM:F0}m, speed={input.SpeedKmh:F0}km/h, remDist={input.RemainingDistanceKm:F1}km, idx={input.CurrentManeuverIndex}/{input.TotalManeuvers}");

        await _bleManager.ExecuteNavigationActionAsync(
            "SendNavigationUpdateAsync", input);
    }

    // -- Helpers

    private NavigationManeuverInfo? GetManeuverInfo()
    {
        Maneuver? m = _navigation.CurrentManeuver;
        if (m == null) return null;

        return new NavigationManeuverInfo
        {
            Index = _navigation.CurrentManeuverIndex,
            Total = _navigation.TotalManeuvers,
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
    }
}