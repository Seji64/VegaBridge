using Serilog;
using VegaBridgeApp.Models.BLE;
using VegaBridgeApp.Models.Navigation;
using VegaBridgeApp.Models.Valhalla;
using VegaBridgeApp.Services.Navigation;

namespace VegaBridgeApp.Services.BLE;

/// <summary>
/// Mediator bridging NavigationService (domain) → BleManagerService (transport).
///
/// Implements <see cref="INavigationSink"/> so the <see cref="NavigationService"/>
/// can report navigation events directly to the BLE layer.
/// </summary>
public class BleNavigationCoordinator : INavigationSink, IDisposable
{
    private readonly NavigationService _navigation;
    private readonly BleManagerService _bleManager;

    // Throttling for periodic status updates (SM frames)
    private DateTimeOffset _lastStatusSent = DateTimeOffset.MinValue;
    private readonly TimeSpan _statusThrottleInterval = TimeSpan.FromMilliseconds(500);

    // Serializes BLE frame writes so concurrent update chains cannot interleave.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

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

        _navigation.AddSink(this);

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
        _navigation.RemoveSink(this);

        _isNavigating = false;
        _currentManeuver = null;
        _currentStatus = null;
        _sendGate.Dispose();
    }

    // ─── INavigationSink ─────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task OnStartAsync(NavigationStartInfo start)
    {
        _currentManeuver = GetManeuverInfo();
        if (_currentManeuver == null)
            return;

        _isNavigating = true;
        _currentStatus = null;

        NavigationStartInput input = new()
        {
            TotalDistanceKm = start.TotalDistanceKm,
            TotalTimeMin = start.TotalTimeMin,
            UpcomingManeuvers = [],
            StartLatitude = start.StartLatitude,
            StartLongitude = start.StartLongitude
        };

        BleCommandLogger.Log($"NAV START: distance={input.TotalDistanceKm:F1}km, time={input.TotalTimeMin:F0}min, maneuvers={start.ManeuverCount}");

        await _bleManager.ExecuteNavigationActionAsync(
            "SendNavigationStartAsync", input);
    }

    /// <inheritdoc />
    public async Task OnManeuverAsync(NavigationManeuverInfo maneuver)
    {
        _currentManeuver = maneuver;
        await SendUpdateAsync();
    }

    /// <inheritdoc />
    public async Task OnStatusAsync(NavigationStatus status)
    {
        _currentStatus = status;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastStatusSent < _statusThrottleInterval)
            return;

        _lastStatusSent = now;
        await SendUpdateAsync();
    }

    /// <summary>
    /// Re-sends the current maneuver + status to the bike after a reconnect.
    /// Called when the app returns to the foreground and the BLE link was
    /// rebuilt – the display otherwise keeps showing stale instructions.
    /// </summary>
    public async Task ResendCurrentStateAsync()
    {
        if (!_isNavigating || _currentManeuver == null || _currentStatus == null)
            return;

        Log.Information("Resending navigation state after reconnect");
        await SendUpdateAsync();
    }

    /// <inheritdoc />
    public async Task OnOffRouteAsync(double lat, double lon, double distM)
    {
        OffRouteAlertInput input = new()
        {
            DistanceMeters = distM,
            Latitude = lat,
            Longitude = lon,
            DetectedAt = DateTimeOffset.UtcNow
        };

        await _bleManager.ExecuteNavigationActionAsync(
            nameof(IBleDevicePlugin.SendOffRouteAlertAsync),
            input);
    }

    /// <inheritdoc />
    public async Task OnFinishAsync()
    {
        _isNavigating = false;
        _currentManeuver = null;
        _currentStatus = null;

        await _bleManager.ExecuteNavigationFinishAsync();
    }

    /// <inheritdoc />
    public async Task OnCancelAsync()
    {
        _isNavigating = false;
        _currentManeuver = null;
        _currentStatus = null;

        await _bleManager.ExecuteNavigationStopAsync();
    }

    /// <inheritdoc />
    public Task OnRouteUpdatedAsync(RouteResponse response)
    {
        // BLE does not need the route geometry; maneuvers/status flow through the other sinks.
        return Task.CompletedTask;
    }

    // -- Helpers

    private async Task SendUpdateAsync()
    {
        if (!_isNavigating || _currentManeuver == null || _currentStatus == null)
            return;

        NavigationStatus status = _currentStatus;
        NavigationManeuverInfo maneuver = _currentManeuver;

        string intersectionName = maneuver.StreetNames.FirstOrDefault() ?? string.Empty;
        string street = intersectionName;

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

        await _sendGate.WaitAsync();
        try
        {
            await _bleManager.ExecuteNavigationActionAsync(
                "SendNavigationUpdateAsync", input);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    // -- Helpers

    private NavigationManeuverInfo? GetManeuverInfo()
    {
        Maneuver? m = _navigation.CurrentManeuver;
        if (m == null)
            return null;

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
