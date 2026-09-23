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

    // ── Send policy: on-change + backup interval ──────────────────────────
    // Field log 2026-09-23: sustained ~2–3 frames/s (NAVI+SM per GPS tick)
    // coincided with the peripheral's W2R queue clogging for 2.5–4.3 min.
    // The official MV app is event-driven (NAVI on maneuver change, SM on
    // status change) – our 1 Hz updates were VegaBridge's own traffic.
    // New policy: NAVI only when the instruction itself changes, SM only
    // when a value crosses its distance bucket (suppressed at standstill,
    // mirroring the official app's standstill pause), plus one full resend
    // every 30 s as backup. PING (30 s) is the only constant traffic.
    //
    // SM buckets are distance-adaptive (field requirement): within 2 km of
    // the next maneuver the display must stay fresh – 50 m buckets give
    // ~1.8 s updates at 100 km/h, ~6 s at 30 km/h (high-frequency without
    // the old 1 Hz overload). Beyond 2 km, 200 m is enough (~7 s at 100
    // km/h, ~24 s at 30 km/h). The bucket-size change at the 2 km boundary
    // changes the bucket index and triggers one immediate update.
    private string _lastNaviSignature = "";
    private int _lastSmRemBucket = int.MinValue;
    private int _lastSmDistTurnBucket = int.MinValue;
    private DateTimeOffset _lastFullUpdateAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan BackupUpdateInterval = TimeSpan.FromSeconds(30);
    private const int SmNearZoneM = 2000;  // approach zone: next maneuver within 2 km
    private const int SmNearBucketM = 50;  // approach zone: 50 m buckets
    private const int SmFarBucketM = 200;  // beyond 2 km: 200 m buckets
    // Official app pauses SM/NAVI traffic when stopped (pklg gaps up to >10 s,
    // spec §6.1). Below this speed, SM distance-bucket crossings (GPS jitter
    // at a red light) are NOT a send trigger – only NAVI signature changes
    // and the 30 s backup resend go out.
    private const double StandstillKmh = 5;

    // Serializes BLE frame writes so concurrent update chains cannot interleave.
    // Send-Gate: if 1, a BLE write is in progress. New frames are discarded
    // immediately (backpressure) instead of queuing behind a stuck write.
    private int _isWriting = 0;

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
        ResetSendPolicyState(); // new session – first update must always send

        NavigationStartInput input = new()
        {
            TotalDistanceKm = start.TotalDistanceKm,
            TotalTimeMin = start.TotalTimeMin,
            UpcomingManeuvers = [],
            StartLatitude = start.StartLatitude,
            StartLongitude = start.StartLongitude
        };

        Log.Information("BLE-LOGGER: {Line}", $"NAV START: distance={input.TotalDistanceKm:F1}km, time={input.TotalTimeMin:F0}min, maneuvers={start.ManeuverCount}");

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
    /// force: true bypasses the on-change dedup.
    /// </summary>
    public async Task ResendCurrentStateAsync()
    {
        if (!_isNavigating || _currentManeuver == null || _currentStatus == null)
            return;

        Log.Information("Resending navigation state after reconnect");
        await SendUpdateAsync(force: true);
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
        ResetSendPolicyState();

        await _bleManager.ExecuteNavigationFinishAsync();
    }

    /// <inheritdoc />
    public async Task OnCancelAsync()
    {
        _isNavigating = false;
        _currentManeuver = null;
        _currentStatus = null;
        ResetSendPolicyState();

        await _bleManager.ExecuteNavigationStopAsync();
    }

    /// <inheritdoc />
    public Task OnRouteUpdatedAsync(RouteResponse response)
    {
        // BLE does not need the route geometry; maneuvers/status flow through the other sinks.
        return Task.CompletedTask;
    }

    // -- Helpers

    private async Task SendUpdateAsync(bool force = false)
    {
        if (!_isNavigating || _currentManeuver == null || _currentStatus == null)
            return;

        NavigationStatus status = _currentStatus;
        NavigationManeuverInfo maneuver = _currentManeuver;

        string intersectionName = maneuver.StreetNames.FirstOrDefault() ?? string.Empty;
        string street = intersectionName;

        NavigationUpdateInput input = new()
        {
            // Valhalla emits kDestination (type 4) at the end of EVERY leg,
            // including intermediate waypoints ("Arrive" at the leg end).
            // Only the very last maneuver of the route is the real
            // destination – mapping a waypoint arrival to the finish icon
            // makes the bike display FINISH mid-route (observed on a real
            // ride; the official MV app shows the same quirk for the same
            // reason). Map waypoint arrivals to "straight" instead.
            ManeuverIcon = NavigationIconMapper.GetSemanticIcon(
                maneuver.ValhallaType == 4 && maneuver.Index < maneuver.Total - 1
                    ? 0 // kNone → straight
                    : maneuver.ValhallaType),
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

        // On-change dedup + 30 s backup resend (send-policy comment above).
        // force=true (reconnect/foreground) always sends – the bike's
        // display may be stale. SM buckets switch between 50 m (within
        // 2 km of the next maneuver) and 200 m (beyond) – see constants.
        string naviSignature = $"{maneuver.Index}:{input.ManeuverIcon}:{input.InstructionText}:{street}";
        int smBucketM = status.DistanceToNextTurnM <= SmNearZoneM ? SmNearBucketM : SmFarBucketM;
        int remBucket = (int)(status.RemainingDistanceKm * 1000) / smBucketM;
        int distTurnBucket = (int)(status.DistanceToNextTurnM / smBucketM);
        double sinceBackup = (DateTimeOffset.UtcNow - _lastFullUpdateAt).TotalSeconds;
        bool standstill = status.SpeedKmh < StandstillKmh;
        bool smBucketChanged = remBucket != _lastSmRemBucket || distTurnBucket != _lastSmDistTurnBucket;

        // Standstill throttle: while stopped, GPS jitter can cross distance
        // buckets – those are no send trigger. NAVI changes and the 30 s
        // backup resend still flow, so the display stays fresh and the link
        // keeps its periodic health check.
        if (!force &&
            naviSignature == _lastNaviSignature &&
            !(smBucketChanged && !standstill) &&
            sinceBackup < BackupUpdateInterval.TotalSeconds)
        {
            Log.Debug("Navigation update skipped – no NAVI/SM change (backup in {S:F0}s, standstill={S})",
                BackupUpdateInterval.TotalSeconds - sinceBackup, standstill);
            return;
        }

        Log.Information("BLE-LOGGER: {Line}", $"NAV UPDATE INPUT: icon={input.ManeuverIcon}, instr={input.InstructionText}, street={input.StreetName}, dist={input.DistanceToTurnM:F0}m, speed={input.SpeedKmh:F0}km/h, remDist={input.RemainingDistanceKm:F1}km, idx={input.CurrentManeuverIndex}/{input.TotalManeuvers}");

        // Send-Gate: if a BLE write is already in progress, discard this frame
        // immediately. A 1-second-old navi update is useless — it would only
        // clog the buffer for the next fresh update.
        if (Interlocked.Exchange(ref _isWriting, 1) == 1)
        {
            Log.Debug("Send gate busy – discarding stale navigation update");
            return;
        }
        try
        {
            await _bleManager.ExecuteNavigationActionAsync(
                "SendNavigationUpdateAsync", input);

            // Remember what went out. Even if the write failed, the 30 s
            // backup interval re-sends – a stuck queue cannot starve the
            // display between resends.
            _lastNaviSignature = naviSignature;
            _lastSmRemBucket = remBucket;
            _lastSmDistTurnBucket = distTurnBucket;
            _lastFullUpdateAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            Interlocked.Exchange(ref _isWriting, 0);
        }
    }

    private void ResetSendPolicyState()
    {
        _lastNaviSignature = "";
        _lastSmRemBucket = int.MinValue;
        _lastSmDistTurnBucket = int.MinValue;
        _lastFullUpdateAt = DateTimeOffset.MinValue;
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
