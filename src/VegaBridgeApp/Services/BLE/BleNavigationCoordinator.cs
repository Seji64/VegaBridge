using Serilog;
using VegaBridgeApp.Models.BLE;
using VegaBridgeApp.Models.Valhalla;
using VegaBridgeApp.Services.Navigation;

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

        NavigationStartInput input = new()
        {
            TotalDistanceKm = _navigation.TotalDistanceKm,
            TotalTimeMin = _navigation.TotalTimeMin,
            UpcomingManeuvers = []
        };

        await _bleManager.ExecuteNavigationActionAsync(
            "SendNavigationStartAsync", input);
    }

    private async Task SendUpdateAsync()
    {
        if (!_isNavigating || _currentManeuver == null || _currentStatus == null)
            return;

        NavigationStatus status = _currentStatus;
        NavigationManeuverInfo maneuver = _currentManeuver;

        NavigationUpdateInput input = new()
        {
            ManeuverIcon = "", // filled by BleManagerService via plugin mapping
            InstructionText = maneuver.Instruction,
            StreetName = maneuver.StreetNames.FirstOrDefault() ?? string.Empty,
            DistanceToTurnM = status.DistanceToNextTurnM,
            SpeedKmh = status.SpeedKmh,
            RemainingDistanceKm = status.RemainingDistanceKm,
            RemainingTimeMin = status.RemainingTimeMin,
            CurrentManeuverIndex = maneuver.Index,
            TotalManeuvers = maneuver.Total,
            IsFinal = maneuver.Index >= maneuver.Total - 1 && status.DistanceToNextTurnM <= 0
        };

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
            BLEIcon = "",
            RoundaboutExit = m.RoundaboutExit
        };
    }
}