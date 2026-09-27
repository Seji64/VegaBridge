using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Serilog;
using Shiny.BluetoothLE;
using VegaBridgeApp.Models.BLE;
using VegaBridgeApp.Services.BLE.Plugins;

namespace VegaBridgeApp.Services.BLE;

/// <summary>
/// Central BLE service that manages scanning and connecting using Shiny.BluetoothLE.
/// Implements a reactive state machine to provide a predictable API for the UI.
/// </summary>
public class BleManagerService(IBleManager bleManager, IEnumerable<IBleDevicePlugin> plugins) : IDisposable
{
    private IPeripheral? _activePeripheral;
    private IBleDevicePlugin? _activePlugin;
    private IDisposable? _scanSubscription;
    private IDisposable? _connectionSubscription;
    private IDisposable? _notificationSubscription;
    private CancellationTokenSource? _scanTimeoutCts;
    private CancellationTokenSource? _retryCts;

    // Maintain our own dictionary of discovered peripherals for reliable access
    private readonly ConcurrentDictionary<string, IPeripheral> _discoveredPeripherals = new();
    
    private readonly IEnumerable<IBleDevicePlugin> _plugins = plugins;

    // Cooldown: prevent reconnect storms when BLE writes fail repeatedly.
    private DateTimeOffset _lastInvalidateAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan InvalidateCooldown = TimeSpan.FromSeconds(15);
    // Expose active plugin for advanced access (e.g., session ID)
    public IBleDevicePlugin? ActivePlugin => _activePlugin;

    // ── Reactive State ──────────────────────────────────────────────────

    private readonly BehaviorSubject<BleConnectionState> _state = new(BleConnectionState.Idle);
    public IObservable<BleConnectionState> State => _state.AsObservable();
    private BleConnectionState CurrentState => _state.Value;

    private readonly BehaviorSubject<IReadOnlyList<BleDeviceInfo>> _devices = new([]);
    public IObservable<IReadOnlyList<BleDeviceInfo>> Devices => _devices.AsObservable();
    public IReadOnlyList<BleDeviceInfo> CurrentDevices => _devices.Value;

    private readonly BehaviorSubject<string> _errorMessage = new(string.Empty);
    public IObservable<string> ErrorMessages => _errorMessage.AsObservable();

    // ── Public API ────────────────────────────────────────────────────

    public bool IsAnyDeviceConnected => bleManager.GetConnectedPeripherals().Any();

    public async Task<bool> RequestAccessAsync()
    {
        try
        {
            AccessState accessState = await bleManager.RequestAccessAsync();
            if (accessState == AccessState.Available) return true;
            UpdateError("BLE access denied. Please check system permissions.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Critical error requesting BLE access");
            UpdateError($"Access error: {ex.Message}");
            return false;
        }
    }

    public async Task StartScanningAsync()
    {
        StopScanning();

        if (!await RequestAccessAsync()) return;

        try
        {
            _state.OnNext(BleConnectionState.Scanning);
            
            _discoveredPeripherals.Clear();
            if (_activePeripheral != null)
            {
                _discoveredPeripherals[_activePeripheral.Uuid] = _activePeripheral;
            }

            // iOS quirk: once the OS has established a connection (e.g. via
            // state restoration) the peripheral often stops advertising, so
            // a pure scan never sees it again. Poll periodically during the
            // scan to catch peripherals that iOS connected in the background.
            _scanTimeoutCts = new CancellationTokenSource();
            CancellationToken scanToken = _scanTimeoutCts.Token;

            _ = Task.Run(async () =>
            {
                while (!scanToken.IsCancellationRequested)
                {
                    RefreshConnectedPeripherals();
                    await Task.Delay(TimeSpan.FromSeconds(5), scanToken);
                }
            }, scanToken);

            // Initial check (immediate)
            RefreshConnectedPeripherals();

            Log.Information("BLE scanning started");
            _scanSubscription = bleManager.ScanForUniquePeripherals().Subscribe(UpdateDeviceFromScanResult);
            
            await Task.Delay(TimeSpan.FromSeconds(30), scanToken);
            if (CurrentState == BleConnectionState.Scanning)
            {
                Log.Information("BLE scan automatic timeout reached");
                StopScanning();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start BLE scan");
            UpdateError($"Could not start scan: {ex.Message}");
        }
    }

    /// <summary>
    /// Falls back to GATT service-UUID based plugin matching. Needed for
    /// peripherals surfaced via <c>GetConnectedPeripherals()</c> whose name
    /// is still "Unknown" (iOS did not read it while connected through the
    /// OS) – name-based IsCompatible matching would miss them.
    /// </summary>
    private async Task<IBleDevicePlugin?> SelectPluginByServiceUuidAsync(IPeripheral peripheral)
    {
        try
        {
            IReadOnlyList<BleServiceInfo>? services = await peripheral.GetServices().FirstOrDefaultAsync();
            if (services is null || services.Count == 0)
            {
                Log.Warning("BLE: no GATT services found for {Uuid} – plugin fallback failed", peripheral.Uuid);
                return null;
            }

            foreach (IBleDevicePlugin plugin in _plugins)
            {
                // BleServiceInfo.Uuid is a short/long string UUID (e.g. "180D"
                // or "0000180d-..."); plugin.ServiceUuid is a Guid. Compare
                // case-insensitively on the full 36-char form.
                string pluginUuid = plugin.ServiceUuid.ToString().ToUpperInvariant();
                if (services.Any(s => NormalizeUuid(s.Uuid) == pluginUuid))
                {
                    Log.Information("BLE: plugin {Plugin} matched via service UUID {Uuid} for {Device}",
                        plugin.DisplayName, plugin.ServiceUuid, peripheral.Uuid);
                    return plugin;
                }
            }

            Log.Warning("BLE: no plugin matched the service list of {Uuid} ({Services} services)",
                peripheral.Uuid, services.Count);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BLE: service-UUID plugin matching failed for {Uuid}", peripheral.Uuid);
            return null;
        }
    }

    /// <summary>
    /// Normalizes a GATT UUID string to the full 36-char upper-case form,
    /// so short forms ("180D") and long forms ("0000180d-0000-1000-8000-00805f9b34fb")
    /// compare equal.
    /// </summary>
    private static string NormalizeUuid(string uuid)
    {
        string value = uuid.Trim().ToUpperInvariant();
        if (value.Length == 4)
            value = $"0000{value}-0000-1000-8000-00805F9B34FB";
        return value;
    }

    public void StopScanning()
    {
        Log.Information("Stopping BLE scan");
        _scanTimeoutCts?.Cancel();
        _scanTimeoutCts?.Dispose();
        _scanTimeoutCts = null;
        _scanSubscription?.Dispose();
        _scanSubscription = null;
        bleManager.StopScan();

        if (CurrentState == BleConnectionState.Scanning)
        {
            _state.OnNext(BleConnectionState.Idle);
        }
    }

    public async Task<bool> ConnectAsync(Guid deviceUuid)
    {
        if (CurrentState == BleConnectionState.Connecting) return false;

        try
        {
            _state.OnNext(BleConnectionState.Connecting);
            Log.Information("Attempting to connect to device {Uuid}", deviceUuid);

            string uuidKey = deviceUuid.ToString();
            if (!_discoveredPeripherals.TryGetValue(uuidKey.ToUpper(), out IPeripheral? peripheral))
            {
                UpdateError("Device not found. Please scan again.", isCritical: false);
                return false;
            }

            await peripheral.ConnectAsync(timeout: TimeSpan.FromSeconds(30));

            _activePeripheral = peripheral;
            
            // Plugin Selection. Name-based matching works for peripherals
            // found via advertising. But GetConnectedPeripherals() can return
            // a device the OS has already connected to, whose name is still
            // "Unknown" (not read yet). Fall back to GATT service-UUID
            // matching against each plugin's ServiceUuid.
            BleDeviceInfo deviceInfo = new() { Uuid = deviceUuid, Name = peripheral.Name ?? "Unknown" };
            _activePlugin = _plugins.FirstOrDefault(p => p.IsCompatible(deviceInfo));

            if (_activePlugin == null)
            {
                _activePlugin = await SelectPluginByServiceUuidAsync(peripheral);
            }

            if (_activePlugin == null)
            {
                Log.Warning("No compatible plugin found for device {Uuid}", deviceUuid);
                UpdateError("Device connected, but no compatible driver found.", isCritical: false);
            }

            _state.OnNext(BleConnectionState.Connected);
            Log.Information("Successfully connected to {Uuid} using plugin {Plugin}", deviceUuid, _activePlugin?.DisplayName ?? "None");
            
            SetupConnectionMonitoring(peripheral);
            SetupNotifications(peripheral);

            // Subscribe to write failures: a dead link (iOS dropped it while
            // the app was in the background) surfaces as a write timeout.
            // Reconnect instead of pinging/sending into the void.
            if (_activePlugin is MvAgustaBlePlugin mvPlugin)
            {
                mvPlugin.WriteFailed -= OnPluginWriteFailed;
                mvPlugin.WriteFailed += OnPluginWriteFailed;
            }

            // After a reconnect the keepalive must resume if a navigation
            // session is active (the plugin tracks _pingShouldRun).
            if (_activePlugin is MvAgustaBlePlugin mv)
            {
                BleConnectedDeviceWrapper wrapper = new(peripheral, mv);
                await mv.EnsurePingRunningAsync(wrapper);
            }

            UpdateDeviceList();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Connection failed for {Uuid}", deviceUuid);
            _state.OnNext(BleConnectionState.Idle);
            UpdateError($"Connection failed: {ex.Message}");
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_activePeripheral == null) return;

        try
        {
            Log.Information("Disconnecting from {Uuid}", _activePeripheral.Uuid);
            
            if (_retryCts != null)
            {
                await _retryCts.CancelAsync();
                _retryCts?.Dispose();
                _retryCts = null;
            }
            _connectionSubscription?.Dispose();
            _connectionSubscription = null;
            _notificationSubscription?.Dispose();
            _notificationSubscription = null;

            await _activePeripheral.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error during disconnection");
        }
        finally
        {
            _activePeripheral = null;
            _activePlugin = null;
            _state.OnNext(BleConnectionState.Idle);
            UpdateDeviceList();
        }
    }

    /// <summary>
    /// Ensures the active BLE connection is still alive and usable. iOS can
    /// silently drop the link while the app is in the background (screen off,
    /// phone in the pocket) – the UI still shows "Connected", but writes time
    /// out. Call this when the app returns to the foreground and before
    /// sending navigation frames; it reconnects when the link is gone.
    /// </summary>
    public async Task<bool> EnsureConnectedAsync()
    {
        if (_activePeripheral == null)
            return false;

        try
        {
            // Shiny reports the current link status; Connected means the
            // GATT link is really alive, anything else (Disconnected,
            // Connecting, etc.) means we must rebuild it.
            if (_activePeripheral.Status == ConnectionState.Connected)
            {
                return true;
            }

            Log.Warning("BLE link lost (status={Status}) – reconnecting", _activePeripheral.Status);
            return await RetryConnectionAsync(Guid.Parse(_activePeripheral.Uuid));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "EnsureConnectedAsync failed");
            return false;
        }
    }

    /// <summary>
    /// Marks the current connection as broken and schedules a reconnect.
    /// Used when a write times out (Arg_TimeoutException) – the classic sign
    /// that iOS dropped the link without firing a disconnect event.
    /// </summary>
    public void InvalidateConnectionAndReconnect()
    {
        IPeripheral? lost = _activePeripheral;
        if (lost == null) return;

        // Cooldown: prevent reconnect storms. Without this, each failed BLE
        // write triggers a reconnect → next write also fails → reconnect again
        // every 3-7s, preventing the link from ever stabilizing.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastInvalidateAt < InvalidateCooldown)
        {
            Log.Debug("InvalidateConnectionAndReconnect: cooldown active – skipping");
            return;
        }
        _lastInvalidateAt = now;
        Log.Warning("Forcing connection state to Idle after write failure");
        _connectionSubscription?.Dispose();
        _connectionSubscription = null;
        _notificationSubscription?.Dispose();
        _notificationSubscription = null;
        _activePeripheral = null;
        _activePlugin = null;
        _state.OnNext(BleConnectionState.Idle);
        UpdateDeviceList();
        UpdateError("Connection lost. Attempting to reconnect...");

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            try
            {
                await RetryConnectionAsync(Guid.Parse(lost.Uuid));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Reconnect after write failure failed");
            }
        });
    }

    // ── Plugin API Proxy ──────────────────────────────────────────────────

    public async Task SendTestFrameAsync()
    {
        if (_activePeripheral == null || _activePlugin == null)
        {
            UpdateError("No connected device or compatible plugin available.");
            return;
        }

        try
        {
            BleConnectedDeviceWrapper wrapper = new(_activePeripheral, _activePlugin);
            Log.Information("BLE-LOGGER: {Line}", "SEND TEST FRAME (via SendTestAsync)");
            await _activePlugin.SendTestAsync(wrapper);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send test frame");
            UpdateError($"Test frame failed: {ex.Message}");
        }
    }

    /// <summary>Outcome of the 30-minute W2R route simulation (see <see cref="BleManagerService.RunW2rRouteSimAsync"/>).</summary>
    public sealed record W2rRouteSimResult(
        int Ticks, int SlowTicks, int FailedTicks, int MaxDrainMs, int? FirstAnomalySecond, string Summary);

    /// <summary>One scripted maneuver of the route simulation timeline.</summary>
    private sealed record SimManeuver(
        string Segment, string Icon, string Instruction, string Street,
        int DurationSec, double SpeedKmh, int DistanceToTurnM);

    /// <summary>
    /// 30-minute route simulation over the real W2R write path: city traffic
    /// (frequent instruction changes) → long B10 highway stretch → simulated
    /// reroute (RENAVI + new instruction set) → city again → destination.
    /// Sends NAVI+SM at the baseline 1 Hz cadence, starts the PING keepalive
    /// via the navigation-start flow (like a real ride), and stops it on
    /// FINISH. Every tick measures how long the write path took to accept
    /// the frame ("drain time"): with Shiny 5.7.2+ a clogged W2R queue shows
    /// up as ~4 s timeouts, so any tick above the 500 ms threshold or a
    /// failed write is logged as a W2R-SIM anomaly and 3+ consecutive
    /// anomalies are flagged as a detected stall. Heartbeats, segment
    /// changes and the final summary keep the exported log readable.
    /// Designed for a display-off run: no UI dependency, progress and
    /// summary are logged under "W2R-SIM" for later analysis.
    /// </summary>
    public async Task<W2rRouteSimResult> RunW2rRouteSimAsync(double? startLat, double? startLon, CancellationToken ct, bool withReroute = true)
    {
        if (_activePeripheral is null || _activePlugin is null)
            return new W2rRouteSimResult(0, 0, 0, 0, null, "W2R-SIM: no connected device");

        BleConnectedDeviceWrapper wrapper = new(_activePeripheral, _activePlugin);
        IPeripheral? wrapperPeripheral = _activePeripheral;
        IBleDevicePlugin? wrapperPlugin = _activePlugin;
        IReadOnlyList<SimManeuver> profile = BuildW2rRouteProfile();
        double totalKm = profile.Sum(m => m.SpeedKmh * m.DurationSec / 3600.0);
        int totalSec = profile.Sum(m => m.DurationSec);

        string simMode = withReroute ? "REROUTE" : "CONTROL (ohne Reroute)";
        Log.Information("BLE-LOGGER: {Line}",
            $"W2R-SIM START: {totalSec / 60}-min route, {totalKm:F1} km (city-1 → B10 → REROUTE → city-2 → ziel), 1 Hz NAVI+SM + PING keepalive, slow threshold 500 ms, mode={simMode}");

        int ticks = 0, slowTicks = 0, failedTicks = 0, maxDrainMs = 0;
        int firstAnomalySecond = 0, consecutive = 0;
        double remainingM = totalKm * 1000;
        int second = 0; // t+ elapsed seconds over the whole run
        bool rerouteSent = false;

        try
        {
            // Navigation start (DEST + REM) starts the PING keepalive – same
            // as a real ride, so keepalive behaviour is part of the test.
            await _activePlugin.SendNavigationStartAsync(wrapper, new NavigationStartInput
            {
                TotalDistanceKm = totalKm,
                TotalTimeMin = totalSec / 60.0,
                StartLatitude = startLat,
                StartLongitude = startLon
            });

            const int slowThresholdMs = 500;

            for (int mi = 0; mi < profile.Count; mi++)
            {
                SimManeuver m = profile[mi];

                // Simulated reroute: off-route alert + new instruction set,
                // injected right before the city-2 segment (end of B10).
                // withReroute=false = control run (Kontrolllauf) – same profile,
                // no off-route alert, to test whether the stall is REROUTE-triggered.
                if (m.Segment == "city-2" && !rerouteSent)
                {
                    rerouteSent = true;
                    if (withReroute)
                    {
                        remainingM += 1600; // the reroute adds 1.6 km
                        if (_activePlugin is not null)
                        {
                            Log.Information("BLE-LOGGER: {Line}",
                                $"W2R-SIM REROUTE t+{FormatSimTm(second)}: RENAVI sent (simulated off-route), new route via city-2, remaining +1.6 km");
                            await _activePlugin.SendOffRouteAlertAsync(wrapper, new OffRouteAlertInput
                            {
                                DistanceMeters = 0,
                                Latitude = startLat ?? 0,
                                Longitude = startLon ?? 0,
                                DetectedAt = DateTimeOffset.UtcNow
                            });
                        }
                        else
                        {
                            Log.Information("BLE-LOGGER: {Line}",
                                $"W2R-SIM REROUTE t+{FormatSimTm(second)}: RENAVI skipped – no active connection");
                        }
                        await Task.Delay(500, ct);
                    }
                    else
                    {
                        Log.Information("BLE-LOGGER: {Line}",
                            $"W2R-SIM CONTROL t+{FormatSimTm(second)}: Reroute deaktiviert (Kontrolllauf) – city-2 ohne Off-Route-Alert");
                        await Task.Delay(500, ct);
                    }
                }

                Log.Information("BLE-LOGGER: {Line}",
                    $"W2R-SIM SEGMENT t+{FormatSimTm(second)}: {m.Segment} ({m.DurationSec}s @ {m.SpeedKmh:F0} km/h, maneuver {mi + 1}/{profile.Count})");

                for (int s = 0; s < m.DurationSec; s++, second++)
                {
                    ct.ThrowIfCancellationRequested();
                    ticks++;

                    double distToTurnM = m.DistanceToTurnM * (1.0 - s / (double)m.DurationSec);
                    remainingM -= m.SpeedKmh * 1000.0 / 3600.0;

                    var input = new NavigationUpdateInput
                    {
                        ManeuverIcon = m.Icon,
                        InstructionText = m.Instruction,
                        StreetName = m.Street,
                        DistanceToTurnM = Math.Max(0, distToTurnM),
                        SpeedKmh = m.SpeedKmh,
                        RemainingDistanceKm = Math.Max(0, remainingM) / 1000.0,
                        RemainingTimeMin = (totalSec - second) / 60.0,
                        CurrentManeuverIndex = mi,
                        TotalManeuvers = profile.Count,
                        IsFinal = mi == profile.Count - 1
                    };

                    // Reconnect-proofing: an in-session reconnect nulls and
                    // reassigns _activePeripheral/_activePlugin mid-run. While
                    // the link is down, skip the write (counted as a failed
                    // tick); when a new connection appears, rebuild the
                    // wrapper so ticks keep flowing on the live link instead
                    // of NRE-ing on the dead objects (see run 20260927, tick 1259+).
                    bool linkDown = _activePeripheral is null || _activePlugin is null;
                    if (linkDown)
                    {
                        Log.Information("BLE-LOGGER: {Line}",
                            $"W2R-SIM LINK DOWN t+{FormatSimTm(second)} seg={m.Segment} tick={ticks} – write skipped (reconnect in progress)");
                    }
                    else if (!ReferenceEquals(wrapperPeripheral, _activePeripheral) || !ReferenceEquals(wrapperPlugin, _activePlugin))
                    {
                        wrapper = new BleConnectedDeviceWrapper(_activePeripheral, _activePlugin);
                        wrapperPeripheral = _activePeripheral;
                        wrapperPlugin = _activePlugin;
                        consecutive = 0;
                        Log.Information("BLE-LOGGER: {Line}",
                            $"W2R-SIM LINK REBUILT t+{FormatSimTm(second)} – continuing on the new connection");
                    }

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool ok;
                    if (linkDown)
                    {
                        ok = false;
                        failedTicks++;
                    }
                    else
                    {
                        // Local snapshot: a disconnect can still null the
                        // field between the check above and this line.
                        IBleDevicePlugin? plugin = _activePlugin;
                        if (plugin is null)
                        {
                            ok = false;
                            failedTicks++;
                        }
                        else
                        {
                            try
                            {
                                await plugin.SendNavigationUpdateAsync(wrapper, input);
                                ok = true;
                            }
                            catch (Exception ex)
                            {
                                ok = false;
                                failedTicks++;
                                Log.Information("BLE-LOGGER: {Line}",
                                    $"W2R-SIM FAIL t+{FormatSimTm(second)} seg={m.Segment} tick={ticks} after {sw.ElapsedMilliseconds} ms ({ex.GetType().Name})");
                            }
                        }
                    }
                    int drainMs = (int)sw.ElapsedMilliseconds;
                    sw.Stop();
                    maxDrainMs = Math.Max(maxDrainMs, drainMs);

                    bool anomalous = !ok || drainMs > slowThresholdMs;
                    if (anomalous)
                    {
                        slowTicks++;
                        if (firstAnomalySecond == 0)
                            firstAnomalySecond = second;
                        consecutive++;
                        if (consecutive >= 3)
                        {
                            Log.Information("BLE-LOGGER: {Line}",
                                $"W2R-SIM STALL: {consecutive} consecutive anomalous ticks, since t+{FormatSimTm(firstAnomalySecond)} (threshold {slowThresholdMs} ms, max drain {maxDrainMs} ms)");
                        }
                        else if (ok)
                        {
                            Log.Information("BLE-LOGGER: {Line}",
                                $"W2R-SIM SLOW t+{FormatSimTm(second)} seg={m.Segment} tick={ticks} drainMs={drainMs}");
                        }
                    }
                    else
                    {
                        consecutive = 0;
                    }

                    if (ticks % 30 == 0)
                    {
                        Log.Information("BLE-LOGGER: {Line}",
                            $"W2R-SIM HB t+{FormatSimTm(second)} seg={m.Segment} tick={ticks} cumMaxDrainMs={maxDrainMs} slow={slowTicks} failed={failedTicks}");
                    }

                    // Keep the 1 Hz cadence: the write plus the plugin's
                    // internal leaky-bucket delays already consumed part of
                    // this second (a clogged write self-paces at ~4 s).
                    long leftoverMs = 1000 - sw.ElapsedMilliseconds;
                    if (leftoverMs > 0)
                        await Task.Delay((int)leftoverMs, ct);
                }
            }

            if (_activePlugin is not null)
                await _activePlugin.SendNavigationFinishAsync(wrapper);
            else
                Log.Information("BLE-LOGGER: {Line}", "W2R-SIM: FINISH skipped – no active connection");

            string summary = $"W2R-SIM DONE: {ticks} ticks over {second}s, {slowTicks} slow / {failedTicks} failed"
                + (firstAnomalySecond > 0
                    ? $", first anomaly t+{FormatSimTm(firstAnomalySecond)}"
                    : "")
                + $", max drain {maxDrainMs} ms, reroute sent";
            Log.Information("BLE-LOGGER: {Line}", summary);
            return new W2rRouteSimResult(
                ticks, slowTicks, failedTicks, maxDrainMs,
                firstAnomalySecond > 0 ? firstAnomalySecond : null, summary);
        }
        catch (OperationCanceledException)
        {
            Log.Information("BLE-LOGGER: {Line}", $"W2R-SIM STOPPED (cancelled) at t+{FormatSimTm(second)} – sending FINISH");
            if (_activePlugin is not null)
            {
                try
                {
                    await _activePlugin.SendNavigationFinishAsync(wrapper);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "W2R-SIM: FINISH after cancel failed");
                }
            }
            else
            {
                Log.Information("BLE-LOGGER: {Line}", "W2R-SIM: FINISH skipped – no active connection");
            }
            string summary = $"W2R-SIM STOPPED after {ticks} ticks / {second}s, {slowTicks} slow / {failedTicks} failed, max drain {maxDrainMs} ms";
            Log.Information("BLE-LOGGER: {Line}", summary);
            return new W2rRouteSimResult(ticks, slowTicks, failedTicks, maxDrainMs, null, summary);
        }
    }

    /// <summary>30-minute simulation route profile (see <see cref="RunW2rRouteSimAsync"/>).</summary>
    private static IReadOnlyList<SimManeuver> BuildW2rRouteProfile()
    {
        const double citySpeed = 35, highwaySpeed = 80, finalSpeed = 25;
        return
        [
            // city-1: 480 s – dense traffic, frequent instruction changes
            new SimManeuver("city-1", "turn-left", "Links abbiegen\nHauptstraße", "Hauptstraße", 48, citySpeed, 300),
            new SimManeuver("city-1", "straight", "Richtung Zentrum\nB 27", "B 27", 40, citySpeed, 300),
            new SimManeuver("city-1", "turn-right", "Rechts abbiegen\nKastanienweg", "Kastanienweg", 48, citySpeed, 300),
            new SimManeuver("city-1", "roundabout-right-1", "Kreisverkehr\nAusfahrt 2", "L 1015", 54, citySpeed, 300),
            new SimManeuver("city-1", "turn-left", "Links abbiegen\nSchwabstraße", "Schwabstraße", 48, citySpeed, 300),
            new SimManeuver("city-1", "straight", "Gerade\nL 1015", "L 1015", 40, citySpeed, 300),
            new SimManeuver("city-1", "turn-right", "Rechts abbiegen\nIndustriestraße", "Industriestraße", 48, citySpeed, 300),
            new SimManeuver("city-1", "roundabout-right-2", "Kreisverkehr\nAusfahrt 1", "K 1234", 54, citySpeed, 300),
            new SimManeuver("city-1", "turn-left", "Links abbiegen\nZielstraße", "Zielstraße", 40, citySpeed, 300),
            new SimManeuver("city-1", "straight", "Auf die B 10\nRichtung Süden", "B 10", 60, citySpeed, 300),
            // B10: 720 s – long highway stretch, one instruction at a time
            new SimManeuver("B10", "straight", "Gerade\nB 10", "B 10", 240, highwaySpeed, 8000),
            new SimManeuver("B10", "straight", "Gerade\nB 10", "B 10", 240, highwaySpeed, 8000),
            new SimManeuver("B10", "straight", "Ausfahrt\nAbfahrt Zentrum", "B 10", 240, highwaySpeed, 8000),
            // city-2: 480 s – new route after the simulated reroute
            new SimManeuver("city-2", "turn-right", "Rechts abbiegen\nRosenstraße", "Rosenstraße", 60, citySpeed, 300),
            new SimManeuver("city-2", "straight", "Gerade\nRosenstraße", "Rosenstraße", 60, citySpeed, 300),
            new SimManeuver("city-2", "turn-left", "Links abbiegen\nLindenstraße", "Lindenstraße", 60, citySpeed, 300),
            new SimManeuver("city-2", "roundabout-right-1", "Kreisverkehr\nAusfahrt 2", "L 1015", 60, citySpeed, 300),
            new SimManeuver("city-2", "turn-right", "Rechts abbiegen\nBahnhofstraße", "Bahnhofstraße", 60, citySpeed, 300),
            new SimManeuver("city-2", "straight", "Gerade\nMarktplatz", "Marktplatz", 60, citySpeed, 300),
            new SimManeuver("city-2", "turn-left", "Links abbiegen\nZielstraße", "Zielstraße", 60, citySpeed, 300),
            new SimManeuver("city-2", "straight", "Ziel in Kürze\nAnkunft", "Zielstraße", 60, citySpeed, 300),
            // final approach: 120 s
            new SimManeuver("final", "straight", "Ziel\nVegaBridge", "Ankunft", 120, finalSpeed, 300)
        ];
    }

    private static string FormatSimTm(int seconds) => $"{seconds / 60:00}:{seconds % 60:00}";

    /// <summary>
    /// Execute a semantic navigation action through the active plugin.
    /// Called by BleNavigationCoordinator.
    /// </summary>
    public async Task ExecuteNavigationActionAsync(string action, object input)
    {
        if (_activePeripheral == null || _activePlugin == null)
        {
            Log.Debug("ExecuteNavigationActionAsync skipped: no active device/plugin");
            return;
        }

        Log.Information("BLE-LOGGER: {Line}", $"NAV ACTION: {action}");

        // The classic "stuck navigation" failure mode: iOS dropped the link
        // while the phone was in the pocket (screen off, app suspended) but
        // no disconnect event arrived. Writing into a dead link then blocks
        // for the write timeout (~10s+), stalls the send gate, and every
        // subsequent update queues behind it – the display freezes on the
        // last instruction. Verify the link is actually alive first; if it
        // is not, rebuild it (or fail fast) instead of writing blindly.
        try
        {
            if (!await EnsureConnectedAsync())
            {
                Log.Warning("ExecuteNavigationActionAsync {Action}: link not healthy, skipping write", action);
                UpdateError("Connection lost. Reconnection attempts failed.", isCritical: false);
                return;
            }

            BleConnectedDeviceWrapper wrapper = new(_activePeripheral!, _activePlugin);

            switch (action)
            {
                case "SendNavigationStartAsync":
                {
                    if (input is NavigationStartInput startInput)
                        await _activePlugin.SendNavigationStartAsync(wrapper, startInput);
                    break;
                }
                case "SendNavigationUpdateAsync":
                {
                    if (input is NavigationUpdateInput updateInput)
                    {
                        await _activePlugin.SendNavigationUpdateAsync(wrapper, updateInput);
                    }
                    break;
                }
                case "SendOffRouteAlertAsync":
                {
                    if (input is OffRouteAlertInput alertInput)
                        await _activePlugin.SendOffRouteAlertAsync(wrapper, alertInput);
                    break;
                }
                default:
                    Log.Warning("Unknown navigation action: {Action}", action);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Write failed. Don't retry (500ms blocks the gate for the
            // entire 1s tick) and don't reconnect (let Shiny's disconnect
            // events handle real connection loss). Just log and let the
            // next GPS tick send a fresh frame.
            Log.Debug(ex, "Write failed for {Action} – next tick will retry", action);
        }
    }

    /// <summary>
    /// Handles destination reached via the active plugin.
    /// </summary>
    public async Task ExecuteNavigationFinishAsync()
    {
        if (_activePeripheral == null || _activePlugin == null) return;

        Log.Information("BLE-LOGGER: {Line}", "NAV ACTION: SendNavigationFinishAsync");

        try
        {
            // Same link-health guard as ExecuteNavigationActionAsync: do not
            // write FINISH into a dead link (blocks the whole send path).
            if (!await EnsureConnectedAsync())
            {
                Log.Warning("ExecuteNavigationFinishAsync: link not healthy, skipping write");
                return;
            }

            BleConnectedDeviceWrapper wrapper = new(_activePeripheral!, _activePlugin);
            await _activePlugin.SendNavigationFinishAsync(wrapper);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send navigation finish");
            UpdateError($"Navigation finish failed: {ex.Message}", isCritical: false);
        }
    }

    /// <summary>
    /// Handles user-cancelled navigation via the active plugin.
    /// </summary>
    public async Task ExecuteNavigationStopAsync()
    {
        if (_activePeripheral == null || _activePlugin == null) return;

        Log.Information("BLE-LOGGER: {Line}", "NAV ACTION: SendNavigationStopAsync");

        try
        {
            // Same link-health guard as ExecuteNavigationActionAsync.
            if (!await EnsureConnectedAsync())
            {
                Log.Warning("ExecuteNavigationStopAsync: link not healthy, skipping write");
                return;
            }

            BleConnectedDeviceWrapper wrapper = new(_activePeripheral!, _activePlugin);
            await _activePlugin.SendNavigationStopAsync(wrapper);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send navigation stop");
            UpdateError($"Navigation stop failed: {ex.Message}", isCritical: false);
        }
    }

    private void RefreshConnectedPeripherals()
    {
        try
        {
            foreach (IPeripheral connected in bleManager.GetConnectedPeripherals())
            {
                string key = connected.Uuid.ToUpper();
                if (!_discoveredPeripherals.ContainsKey(key))
                {
                    _discoveredPeripherals[key] = connected;
                    Log.Information("BLE: OS-connected peripheral found: {Uuid} ({Name})",
                        connected.Uuid, connected.Name ?? "Unknown");
                }
            }
            UpdateDeviceList();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "BLE: RefreshConnectedPeripherals failed (non-fatal)");
        }
    }

    // ── Connection Monitoring & Retry ─────────────────────────────────────

    private void SetupConnectionMonitoring(IPeripheral peripheral)
    {
        _connectionSubscription?.Dispose();

        // Monitor both disconnects and connection failures
        var disconnectSub = peripheral.WhenDisconnected()
            .Subscribe(_ => HandleUnexpectedDisconnection(peripheral));
        var failSub = peripheral.WhenConnectionFailed()
            .Subscribe(ex =>
            {
                Log.Warning("Connection failed for {Uuid}: {Error}", peripheral.Uuid, ex.Message);
                HandleUnexpectedDisconnection(peripheral);
            });

        // Combine into single disposable for cleanup
        _connectionSubscription = System.Reactive.Disposables.Disposable.Create(() =>
        {
            disconnectSub.Dispose();
            failSub.Dispose();
        });
    }

    private void SetupNotifications(IPeripheral peripheral)
    {
        _notificationSubscription?.Dispose();

        if (_activePlugin == null) return;

        Log.Information("Setting up notifications for {Uuid} using plugin {Plugin}", peripheral.Uuid, _activePlugin.DisplayName);

        // Use NotifyCharacteristic to subscribe to GATT notifications
        // Signature: NotifyCharacteristic(serviceUuid, characteristicUuid, autoSubscribe)
        _notificationSubscription = peripheral.NotifyCharacteristic(
                _activePlugin.ServiceUuid.ToString(), 
                _activePlugin.ReadCharacteristicUuid)
            .Subscribe(result =>
            {
                if (result.Data != null) _activePlugin.OnDataReceived(result.Data);
            });
    }

    private void HandleUnexpectedDisconnection(IPeripheral peripheral)
    {
        try
        {
            if (_activePeripheral == null) return;

            Log.Warning("Unexpected disconnection detected for {Uuid}", peripheral.Uuid);
            _state.OnNext(BleConnectionState.Idle);
            UpdateDeviceList();
            UpdateError("Connection lost. Attempting to reconnect...");

            _ = Task.Run(async () =>
            {
                try
                {
                    await RetryConnectionAsync(Guid.Parse(peripheral.Uuid));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Reconnect after unexpected disconnection failed");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Critical error during unexpected disconnection handling for {Uuid}", peripheral.Uuid);
            UpdateError($"Error during reconnection: {ex.Message}");
        }
    }

    /// <summary>
    /// A plugin write failed – the classic sign that iOS dropped the BLE
    /// link while the app was in the background (write timeout without a
    /// disconnect event). Reconnect immediately.
    /// </summary>
    private void OnPluginWriteFailed(Exception ex)
    {
        Log.Warning("Plugin write failed ({Message}) – invalidating connection", ex.Message);
        InvalidateConnectionAndReconnect();
    }

    private async Task<bool> RetryConnectionAsync(Guid deviceUuid)
    {
        const int maxRetries = 3;
        int attempt = 0;
        _retryCts?.Cancel();
        _retryCts?.Dispose();
        _retryCts = new CancellationTokenSource();
        CancellationToken token = _retryCts.Token;

        while (attempt < maxRetries && !token.IsCancellationRequested)
        {
            attempt++;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), token);
                if (await ConnectAsync(deviceUuid))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                Log.Warning(ex, "Retry attempt {Attempt} failed for {Uuid}", attempt, deviceUuid);
            }
        }

        if (!token.IsCancellationRequested)
        {
            UpdateError("Connection lost. Reconnection attempts failed.");
        }
        return false;
    }

    private void UpdateDeviceFromScanResult(IPeripheral result)
    {
        _discoveredPeripherals[result.Uuid.ToUpper()] = result;
        UpdateDeviceList();
    }

    private void UpdateDeviceList()
    {
        List<BleDeviceInfo> list =
        [
            .. _discoveredPeripherals.Values
                // OS-connected peripherals (retrieved without advertising)
                // may have an unknown name on first sight – keep them, the
                // user recognizes the bike and the name is read on connect.
                .Where(p => (!string.IsNullOrWhiteSpace(p.Name) && p.Name != "Unknown") || p.IsConnected())
                .Select(p => 
                {
                    BleDeviceInfo deviceInfo = new()
                    {
                        Uuid = Guid.Parse(p.Uuid),
                        // OS-connected peripherals may not have a name yet
                        // (iOS reads it on connect). Never pass null into
                        // the non-nullable Name property – "Unknown" keeps
                        // plugin matching null-safe.
                        Name = string.IsNullOrWhiteSpace(p.Name) ? "Unknown" : p.Name,
                        IsConnected = p.IsConnected(),
                        LastSeen = DateTime.Now
                    };
                    
                    // Determine brand based on compatible plugin
                    IBleDevicePlugin? plugin = _plugins.FirstOrDefault(pl => pl.IsCompatible(deviceInfo));
                    deviceInfo.Brand = plugin?.BrandName;
                    
                    return deviceInfo;
                })
        ];
        _devices.OnNext(list);
    }

    private void UpdateError(string message, bool isCritical = true)
    {
        _errorMessage.OnNext(message);
        if (isCritical)
        {
            _state.OnNext(BleConnectionState.Error);
        }
    }

    public void Dispose()
    {
        StopScanning();
        _connectionSubscription?.Dispose();
        _notificationSubscription?.Dispose();
        _retryCts?.Cancel();
        _retryCts?.Dispose();
        _state.Dispose();
        _devices.Dispose();
        _errorMessage.Dispose();
    }

    // ── HAL Implementation ────────────────────────────────────────────────

    private class BleConnectedDeviceWrapper(IPeripheral peripheral, IBleDevicePlugin plugin) : IBleConnectedDevice
    {
        public Guid Uuid => Guid.Parse(peripheral.Uuid);
        public string Name => peripheral.Name ?? "Unknown";

        public async Task WriteAsync(string characteristicUuid, byte[] data, bool withResponse)
        {
            string serviceUuid = plugin.ServiceUuid.ToString();
            try
            {
                await peripheral.WriteCharacteristicAsync(serviceUuid, characteristicUuid, data, withResponse);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to write to characteristic {characteristicUuid} on service {serviceUuid}: {ex.Message}", ex);
            }
        }

        public async Task<byte[]?> ReadAsync(string characteristicUuid)
        {
            string serviceUuid = plugin.ServiceUuid.ToString();
            try
            {
                BleCharacteristicResult result = await peripheral.ReadCharacteristicAsync(serviceUuid, characteristicUuid);
                return result.Data;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to read characteristic {characteristicUuid} on service {serviceUuid}: {ex.Message}", ex);
            }
        }
    }
}
