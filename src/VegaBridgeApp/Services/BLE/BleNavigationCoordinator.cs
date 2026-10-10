using System.Reactive.Linq;
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

    // Send policy (official MV Ride capture: 289 NAVI+SM pairs in 5.7 min):
    // every ~1 Hz GPS tick sends the FULL state (NAVI + SM; the plugin adds
    // the SM1 arrival-time frame every 30 s), so a frame lost to a W2R stall
    // is healed by the next tick.
    // 900 ms instead of 1000 ms: 1 Hz GPS fixes arrive with jitter, a strict
    // 1 s throttle silently skipped every other fix.
    private DateTimeOffset _lastStatusSent = DateTimeOffset.MinValue;
    private readonly TimeSpan _statusThrottleInterval = TimeSpan.FromMilliseconds(900);

    // Destination of the running session – DEST is re-sent after reroutes
    // and after a (re)connect mid-session.
    private double? _destinationLat;
    private double? _destinationLon;
    private readonly IDisposable _linkUpSubscription;

    // ─── PING keepalive (fills pauses only) ───────────────────────────────
    // The plugin used to own this loop; it now only sends the frame. Each
    // tick resolves the current live connection through the manager, so a
    // reconnect needs no re-arming: the next tick just picks up the new
    // link, and ticks while the link is down are no-ops.
    private CancellationTokenSource? _keepAliveCts;
    private Task? _keepAliveTask;
    // PING is skipped while NAVI frames were delivered in the last 5 s – with
    // the 1 Hz full-state policy it only goes out when the tick stream pauses.
    private DateTimeOffset _lastNaviWriteAt = DateTimeOffset.MinValue;

    private async Task StartKeepAliveAsync()
    {
        // Stop the old loop first so an old tick cannot overlap the new
        // loop's (route restart mid-session).
        Task? oldTask = _keepAliveTask;
        _keepAliveCts?.Cancel();
        _keepAliveCts?.Dispose();
        _keepAliveCts = null;
        _keepAliveTask = null;
        if (oldTask != null)
        {
            try { await oldTask; }
            catch (OperationCanceledException) { /* expected when stopping */ }
        }

        CancellationTokenSource cts = new();
        _keepAliveCts = cts;
        _keepAliveTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    // Throws OperationCanceledException on stop → loop exits.
                    await Task.Delay(TimeSpan.FromSeconds(15), cts.Token);

                    if (DateTimeOffset.UtcNow - _lastNaviWriteAt < TimeSpan.FromSeconds(5))
                        continue; // NAVI+SM already warmed the W2R path
                    await _bleManager.ExecuteKeepAliveAsync(); // no-op without a live link
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Keepalive tick loop error – PING keepalive stopped");
            }
        }, cts.Token);
    }

    private async Task StopKeepAliveAsync()
    {
        _keepAliveCts?.Cancel();
        _keepAliveCts?.Dispose();
        _keepAliveCts = null;

        Task? task = _keepAliveTask;
        _keepAliveTask = null;
        if (task == null)
            return;

        try { await task; }
        catch (OperationCanceledException) { /* expected */ }
    }

    // Serializes BLE frame writes so concurrent update chains cannot interleave.
    // Send-Gate: if 1, a BLE write is in progress. New frames are discarded
    // immediately (backpressure) instead of queuing behind a stuck write.
    private int _isWriting = 0;

    // Current context
    private NavigationManeuverInfo? _currentManeuver;
    private NavigationStatus? _currentStatus;
    private volatile bool _isNavigating; // read by link-up / FINISH retry off the GPS thread

    public BleNavigationCoordinator(
        NavigationService navigation,
        BleManagerService bleManager)
    {
        _navigation = navigation;
        _bleManager = bleManager;

        Log.Information("BleNavigationCoordinator initializing and subscribing to events...");

        _navigation.AddSink(this);

        // A fresh link (Shiny auto-reconnect, W2R watchdog, late connect)
        // gets the session start re-sent before the next full-state tick.
        _linkUpSubscription = _bleManager.State
            .DistinctUntilChanged()
            .Where(s => s == BleConnectionState.Connected)
            .Subscribe(state => _ = OnLinkUpAsync()); // fire-and-forget: errors are logged inside

        // Sync if already navigating (app restart, late DI resolution).
        if (_navigation.IsNavigating)
        {
            _isNavigating = true;
            _currentManeuver = _navigation.LastManeuverInfo;
            // Keepalive must cover an active session that started before this
            // coordinator subscribed (app restart, late DI resolution).
            _ = StartKeepAliveAsync();
        }

        Log.Information("BleNavigationCoordinator is now active.");
    }

    public void Dispose()
    {
        _navigation.RemoveSink(this);
        _linkUpSubscription.Dispose();

        _isNavigating = false;
        _currentManeuver = null;
        _currentStatus = null;

        // Sync Dispose cannot await – cancel the tick loop, don't wait.
        _keepAliveCts?.Cancel();
        _keepAliveCts?.Dispose();
        _keepAliveCts = null;
    }

    // ─── INavigationSink ─────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task OnStartAsync(NavigationStartInfo start)
    {
        // The first maneuver arrives right after start via OnManeuverAsync
        // (NavigationService fires it inside StartNavigation) – it carries
        // the first NAVI+SM. Keepalive runs regardless of maneuver state.
        _isNavigating = true;
        _currentManeuver = null;
        _currentStatus = null;
        _destinationLat = start.DestinationLatitude;
        _destinationLon = start.DestinationLongitude;

        Log.Information("BLE-LOGGER: {Line}", $"NAV START: distance={start.TotalDistanceKm:F1}km, time={start.TotalTimeMin:F0}min, maneuvers={start.ManeuverCount}");

        await SendStartSequenceAsync(start.TotalDistanceKm);

        // Nav session active → PING keepalive runs (15 s tick, no-op without
        // a live BLE link; resumes on the next tick after a reconnect).
        _lastNaviWriteAt = DateTimeOffset.UtcNow;
        await StartKeepAliveAsync();
    }

    /// <inheritdoc />
    public Task OnManeuverAsync(NavigationManeuverInfo maneuver)
    {
        _currentManeuver = maneuver;
        // NavigationService reports the status of the same GPS fix right
        // after the maneuver – un-throttle it so the new instruction goes out
        // immediately WITH its own distance (not the previous maneuver's).
        _lastStatusSent = DateTimeOffset.MinValue;
        return Task.CompletedTask;
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
        Log.Information("BLE-LOGGER: {Line}", $"OFF-ROUTE ALERT: dist={distM:F0}m, lat={lat:F6}, lon={lon:F6}");
        await _bleManager.ExecuteNavigationOffRouteAlertAsync();
    }

    /// <inheritdoc />
    public async Task OnFinishAsync()
    {
        _isNavigating = false;
        _currentManeuver = null;
        _currentStatus = null;

        await StopKeepAliveAsync();
        await SendFinishAsync(_bleManager.ExecuteNavigationFinishAsync);
    }

    /// <inheritdoc />
    public async Task OnCancelAsync()
    {
        _isNavigating = false;
        _currentManeuver = null;
        _currentStatus = null;

        await StopKeepAliveAsync();
        await SendFinishAsync(_bleManager.ExecuteNavigationStopAsync);
    }

    /// <inheritdoc />
    public async Task OnRouteUpdatedAsync(RouteResponse response)
    {
        // Official capture: every RENAVI is followed by DEST + REM for the new
        // route (10 of 10 reroutes). The route itself is already loaded, so
        // TotalDistanceKm is the new total.
        if (_isNavigating)
            await SendStartSequenceAsync(_navigation.TotalDistanceKm);
    }

    // -- Helpers

    /// <summary>DEST + REM – the session start frames.</summary>
    private Task SendStartSequenceAsync(double remainingKm) =>
        _bleManager.ExecuteNavigationStartAsync(new NavigationStartInput
        {
            TotalDistanceKm = remainingKm,
            TotalTimeMin = _navigation.TotalTimeMin,
            UpcomingManeuvers = [],
            DestinationLatitude = _destinationLat,
            DestinationLongitude = _destinationLon
        });

    private async Task OnLinkUpAsync()
    {
        if (!_isNavigating)
            return;

        Log.Information("BLE link up during navigation – re-sending DEST/REM, full state follows with the next fix");
        await SendStartSequenceAsync(_currentStatus?.RemainingDistanceKm ?? _navigation.TotalDistanceKm);
        _lastStatusSent = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// FINISH ends the session and nothing re-sends it – a frame dropped by a
    /// full W2R buffer would leave the bike on the last instruction. Retries
    /// for ~5 s (the write path forces a write after 3 s of full buffer) and
    /// gives up as soon as a new session has started.
    /// </summary>
    private async Task SendFinishAsync(Func<Task<bool>> send)
    {
        // Let an in-flight NAVI+SM tick finish first, so it cannot land after FINISH.
        for (int i = 0; i < 20 && Volatile.Read(ref _isWriting) == 1; i++)
            await Task.Delay(50);

        for (int attempt = 0; attempt < 5 && !_isNavigating; attempt++)
        {
            if (await send())
                return;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task SendUpdateAsync()
    {
        if (!_isNavigating || _currentManeuver == null || _currentStatus == null)
            return;

        NavigationStatus status = _currentStatus;
        NavigationManeuverInfo maneuver = _currentManeuver;

        string intersectionName = maneuver.StreetNames.FirstOrDefault() ?? string.Empty;

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
            RoundaboutExitCount = maneuver.RoundaboutExitCount,
            InstructionText = maneuver.Instruction,
            StreetName = intersectionName,
            IntersectionName = intersectionName,
            DistanceToTurnM = status.DistanceToNextTurnM,
            SpeedKmh = status.SpeedKmh,
            RemainingDistanceKm = status.RemainingDistanceKm,
            RemainingTimeMin = status.RemainingTimeMin,
            CurrentManeuverIndex = maneuver.Index,
            TotalManeuvers = maneuver.Total,
            IsFinal = maneuver.Index >= maneuver.Total - 1 && status.DistanceToNextTurnM <= 0
        };

        Log.Information("BLE-LOGGER: {Line}", $"NAV UPDATE INPUT: icon={input.ManeuverIcon}, instr={input.InstructionText}, street={input.StreetName}, dist={input.DistanceToTurnM:F0}m, speed={input.SpeedKmh:F0}km/h, remDist={input.RemainingDistanceKm:F1}km, idx={input.CurrentManeuverIndex}/{input.TotalManeuvers}");

        // Send-Gate: if a BLE write is already in progress, discard this frame
        // immediately. A 1-second-old navi update is useless — it would only
        // clog the buffer for the next fresh update.
        if (Interlocked.Exchange(ref _isWriting, 1) == 1)
        {
            Log.Debug("Send gate busy – discarding stale navigation update");
            return;
        }
        bool delivered = false;
        try
        {
            delivered = await _bleManager.ExecuteNavigationUpdateAsync(input);
        }
        finally
        {
            Interlocked.Exchange(ref _isWriting, 0);
        }

        // PING only fills pauses: it is skipped while NAVI frames are being
        // delivered (official app: 1 PING in 5.7 min, during a 3 s pause).
        if (delivered)
            _lastNaviWriteAt = DateTimeOffset.UtcNow;
    }

}
