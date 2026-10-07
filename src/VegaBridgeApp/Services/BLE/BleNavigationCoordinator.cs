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

    // Throttling for periodic status updates (SM frames). ~1 Hz matches the
    // official MV Ride driving cadence; the old 500 ms tick drove W2R at 2–3
    // frames/s and clogged the bike's write-without-response flow control.
    private DateTimeOffset _lastStatusSent = DateTimeOffset.MinValue;
    private readonly TimeSpan _statusThrottleInterval = TimeSpan.FromMilliseconds(1000);

    // Send policy (official MV profile): the NAVI instruction frame is only
    // written when the maneuver signature (index/instruction/street) changes;
    // in between, status ticks refresh SM every tick, SM1 on countdown-bucket
    // change only (on-change gate in the plugin). PING keepalive (15 s) is
    // what keeps the W2R path warm – this coordinator owns the cadence; the
    // plugin only knows the PING frame (SendKeepAliveAsync).
    private string? _lastNaviSig;

    // ─── PING keepalive (15 s tick, official MV profile) ─────────────────
    // The plugin used to own this loop; it now only sends the frame. Each
    // tick resolves the current live connection through the manager, so a
    // reconnect needs no re-arming: the next tick just picks up the new
    // link, and ticks while the link is down are no-ops.
    private CancellationTokenSource? _keepAliveCts;
    private Task? _keepAliveTask;
    // 5 s skip window: a PING landing right after a NAVI+SM burst clogs the
    // W2R queue (observed: PING + NAVI within 23 ms → write failures).
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
                    // 15 s tick (official MV Ride keepalive cadence); throws
                    // OperationCanceledException on stop → loop exits.
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
    private bool _isNavigating;

    public BleNavigationCoordinator(
        NavigationService navigation,
        BleManagerService bleManager)
    {
        _navigation = navigation;
        _bleManager = bleManager;

        Log.Information("BleNavigationCoordinator initializing and subscribing to events...");

        _navigation.AddSink(this);

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
        _lastNaviSig = null; // new session: first update must carry NAVI

        NavigationStartInput input = new()
        {
            TotalDistanceKm = start.TotalDistanceKm,
            TotalTimeMin = start.TotalTimeMin,
            UpcomingManeuvers = [],
            StartLatitude = start.StartLatitude,
            StartLongitude = start.StartLongitude
        };

        Log.Information("BLE-LOGGER: {Line}", $"NAV START: distance={input.TotalDistanceKm:F1}km, time={input.TotalTimeMin:F0}min, maneuvers={start.ManeuverCount}");

        await _bleManager.ExecuteNavigationStartAsync(input);

        // Nav session active → PING keepalive runs (15 s tick, no-op without
        // a live BLE link; resumes on the next tick after a reconnect).
        _lastNaviWriteAt = DateTimeOffset.UtcNow;
        await StartKeepAliveAsync();
    }

    /// <inheritdoc />
    public async Task OnManeuverAsync(NavigationManeuverInfo maneuver)
    {
        _currentManeuver = maneuver;
        // Maneuver change = NAVI signature change → full update (NAVI + SM).
        // Stamp _lastStatusSent so the status tick on the same GPS reading
        // is throttled out instead of doubling the SM write.
        _lastStatusSent = DateTimeOffset.UtcNow;
        await SendUpdateAsync(sendNavi: true);
    }

    /// <inheritdoc />
    public async Task OnStatusAsync(NavigationStatus status)
    {
        _currentStatus = status;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastStatusSent < _statusThrottleInterval)
            return;

        _lastStatusSent = now;

        // Status ticks refresh SM only, unless the maneuver signature has
        // changed since the last NAVI write (covers the first tick of a
        // session and any change that did not arrive via OnManeuverAsync).
        string sig = NaviSignature();
        await SendUpdateAsync(sendNavi: sig != _lastNaviSig);
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
        // Full resync: the rebuilt link has no memory of the last instruction.
        await SendUpdateAsync(sendNavi: true);
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
        _lastNaviSig = null;

        await _bleManager.ExecuteNavigationFinishAsync();
        await StopKeepAliveAsync();
    }

    /// <inheritdoc />
    public async Task OnCancelAsync()
    {
        _isNavigating = false;
        _currentManeuver = null;
        _currentStatus = null;
        _lastNaviSig = null;

        await _bleManager.ExecuteNavigationStopAsync();
        await StopKeepAliveAsync();
    }

    /// <inheritdoc />
    public Task OnRouteUpdatedAsync(RouteResponse response)
    {
        // BLE does not need the route geometry; maneuvers/status flow through the other sinks.
        return Task.CompletedTask;
    }

    // -- Helpers

    private async Task SendUpdateAsync(bool sendNavi)
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
            delivered = await _bleManager.ExecuteNavigationUpdateAsync(input, sendNavi);
        }
        finally
        {
            Interlocked.Exchange(ref _isWriting, 0);
        }

        // Remember the signature a NAVI write was requested with.
        // On a gate-busy discard, remember the signature anyway so we do NOT
        // spin in a 3-second-blockade retry loop on every subsequent 1-Hz tick.
        // A stale NAVI frame is discarded; the next real maneuver change will
        // update the signature and trigger a fresh NAVI write automatically.
        _lastNaviSig = NaviSignature();
        _lastNaviWriteAt = DateTimeOffset.UtcNow;
    }

    // -- Helpers

    /// <summary>
    /// Signature of the NAVI frame (the instruction the display shows). A new
    /// NAVI write is due when the maneuver index or its display content
    /// (instruction/street) changes.
    /// </summary>
    private string NaviSignature()
        => _currentManeuver is not { } m ? "" : $"{m.Index}|{m.Instruction}|{m.StreetNames.FirstOrDefault() ?? ""}";

}
