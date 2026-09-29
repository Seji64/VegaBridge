using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Diagnostics;
using Serilog;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using VegaBridgeApp.Models.BLE;
using VegaBridgeApp.Services.BLE.Plugins;

namespace VegaBridgeApp.Services.BLE;

/// <summary>
/// Central BLE service that manages scanning and connecting using Plugin.BLE.
/// Implements a reactive state machine to provide a predictable API for the UI.
/// </summary>
public class BleManagerService : IDisposable
{
    public BleManagerService(IAdapter adapter, IEnumerable<IBleDevicePlugin> plugins)
    {
        _adapter = adapter;
        _plugins = plugins;

        _adapter.DeviceDiscovered += OnDeviceDiscovered;
        _adapter.DeviceDisconnected += OnAdapterDeviceDisconnected;
        _adapter.DeviceConnectionLost += OnAdapterDeviceConnectionLost;
        _adapter.DeviceConnectionError += OnAdapterDeviceConnectionError;
    }

    private readonly IAdapter _adapter;
    private readonly IEnumerable<IBleDevicePlugin> _plugins;

    private IDevice? _activeIDevice;
    private IBleDevicePlugin? _activePlugin;
    private ICharacteristic? _notificationCharacteristic;
    private EventHandler<CharacteristicUpdatedEventArgs>? _notificationHandler;
    private Guid? _intentionalDisconnectId;
    private CancellationTokenSource? _scanTimeoutCts;
    private CancellationTokenSource? _retryCts;

    // Maintain our own dictionary of discovered peripherals for reliable access
    private readonly ConcurrentDictionary<string, IDevice> _discoveredIDevices = new();
    
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

    public bool IsAnyDeviceConnected => _adapter.ConnectedDevices.Any(d => d.State == DeviceState.Connected);

    public Task<bool> RequestAccessAsync()
    {
        BluetoothState state = CrossBluetoothLE.Current.State;

        if (state is BluetoothState.Off or BluetoothState.Unavailable or BluetoothState.Unauthorized)
        {
            UpdateError($"BLE unavailable: {state}");
            return Task.FromResult(false);
        }

        // Plugin.BLE does not expose a separate permission-request API.
        // On iOS the CoreBluetooth prompt is triggered when the adapter is first used.
        return Task.FromResult(true);
    }

    public async Task StartScanningAsync()
    {
        await StopScanningInternalAsync();

        if (!await RequestAccessAsync()) return;

        try
        {
            _state.OnNext(BleConnectionState.Scanning);
            _discoveredIDevices.Clear();
            RefreshConnectedIDevices();

            _scanTimeoutCts?.Dispose();
            CancellationTokenSource scanCts = new(TimeSpan.FromSeconds(30));
            _scanTimeoutCts = scanCts;

            Log.Information("BLE scanning started (Plugin.BLE)");
            await _adapter.StartScanningForDevicesAsync(cancellationToken: scanCts.Token);

            if (ReferenceEquals(_scanTimeoutCts, scanCts))
            {
                _scanTimeoutCts = null;
                scanCts.Dispose();
            }

            if (CurrentState == BleConnectionState.Scanning)
                _state.OnNext(BleConnectionState.Idle);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start BLE scan");
            UpdateError($"Could not start scan: {ex.Message}");
        }
    }

    /// <summary>
    /// Falls back to GATT service-UUID based plugin matching. Needed for
    /// peripherals surfaced via <c>GetSystemConnectedOrPairedDevices()</c>.
    /// </summary>
    /// <summary>
    /// Falls back to GATT service-UUID based plugin matching. Needed for
    /// peripherals surfaced via <c>GetConnectedIDevices()</c> whose name
    /// is still "Unknown" (iOS did not read it while connected through the
    /// OS) – name-based IsCompatible matching would miss them.
    /// </summary>
    private async Task<IBleDevicePlugin?> SelectPluginByServiceUuidAsync(IDevice peripheral)
    {
        try
        {
            IReadOnlyList<IService> services = await peripheral.GetServicesAsync();
            if (services.Count == 0)
            {
                Log.Warning("BLE: no GATT services found for {Uuid} – plugin fallback failed", peripheral.Id);
                return null;
            }

            foreach (IBleDevicePlugin plugin in _plugins)
            {
                if (services.Any(service => service.Id == plugin.ServiceUuid))
                {
                    Log.Information(
                        "BLE: plugin {Plugin} matched via service UUID {Uuid} for {Device}",
                        plugin.DisplayName,
                        plugin.ServiceUuid,
                        peripheral.Id);
                    return plugin;
                }
            }

            Log.Warning(
                "BLE: no plugin matched the service list of {Uuid} ({Services} services)",
                peripheral.Id,
                services.Count);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BLE: service-UUID plugin matching failed for {Uuid}", peripheral.Id);
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
        _ = StopScanningInternalAsync();
    }

    private async Task StopScanningInternalAsync()
    {
        Log.Information("Stopping BLE scan");

        _scanTimeoutCts?.Cancel();
        _scanTimeoutCts?.Dispose();
        _scanTimeoutCts = null;

        try
        {
            if (_adapter.IsScanning)
                await _adapter.StopScanningForDevicesAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "BLE scan stop failed (non-fatal)");
        }

        if (CurrentState == BleConnectionState.Scanning)
            _state.OnNext(BleConnectionState.Idle);
    }

    public async Task<bool> ConnectAsync(Guid deviceUuid)
    {
        if (CurrentState == BleConnectionState.Connecting) return false;

        try
        {
            _state.OnNext(BleConnectionState.Connecting);
            Log.Information("Attempting to connect to device {Uuid}", deviceUuid);

            string uuidKey = deviceUuid.ToString().ToUpperInvariant();
            if (!_discoveredIDevices.TryGetValue(uuidKey, out IDevice? device))
            {
                UpdateError("Device not found. Please scan again.", isCritical: false);
                return false;
            }

            await StopScanningInternalAsync();

            using CancellationTokenSource connectCts = new(TimeSpan.FromSeconds(30));
            await _adapter.ConnectToDeviceAsync(device, cancellationToken: connectCts.Token);

            return await CompleteConnectionAsync(device);
        }
        catch (OperationCanceledException)
        {
            _state.OnNext(BleConnectionState.Idle);
            UpdateError("Connection timed out.", isCritical: false);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Connection failed for {Uuid}", deviceUuid);
            _state.OnNext(BleConnectionState.Idle);
            UpdateError($"Connection failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> CompleteConnectionAsync(IDevice device)
    {
        _activeIDevice = device;

        BleDeviceInfo deviceInfo = new()
        {
            Uuid = device.Id,
            Name = string.IsNullOrWhiteSpace(device.Name) ? "Unknown" : device.Name
        };

        _activePlugin = _plugins.FirstOrDefault(p => p.IsCompatible(deviceInfo))
                         ?? await SelectPluginByServiceUuidAsync(device);

        if (_activePlugin == null)
        {
            Log.Warning("No compatible plugin found for device {Uuid}", device.Id);
            UpdateError("Device connected, but no compatible driver found.", isCritical: false);
        }

        _state.OnNext(BleDeviceState.Connected);
        Log.Information(
            "Successfully connected to {Uuid} using Plugin.BLE plugin {Plugin}",
            device.Id,
            _activePlugin?.DisplayName ?? "None");

        await SetupNotificationsAsync(device);

        if (_activePlugin is MvAgustaBlePlugin mv)
        {
            BleConnectedDeviceWrapper wrapper = new(device, mv);
            await mv.EnsurePingRunningAsync(wrapper);
        }

        UpdateDeviceList();
        return true;
    }

    public async Task DisconnectAsync()
    {
        IDevice? device = _activeIDevice;
        if (device == null) return;

        try
        {
            Log.Information("Disconnecting from {Uuid}", device.Id);

            if (_retryCts != null)
            {
                await _retryCts.CancelAsync();
                _retryCts.Dispose();
                _retryCts = null;
            }

            _intentionalDisconnectId = device.Id;
            await StopNotificationsAsync();
            await _adapter.DisconnectDeviceAsync(device);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error during disconnection");
        }
        finally
        {
            _intentionalDisconnectId = null;
            _activeIDevice = null;
            _activePlugin = null;
            _state.OnNext(BleConnectionState.Idle);
            UpdateDeviceList();
        }
    }

    private async Task StopNotificationsAsync()
    {
        ICharacteristic? characteristic = _notificationCharacteristic;
        EventHandler<CharacteristicUpdatedEventArgs>? handler = _notificationHandler;

        _notificationCharacteristic = null;
        _notificationHandler = null;

        if (characteristic == null) return;

        if (handler != null)
            characteristic.ValueUpdated -= handler;

        try
        {
            await characteristic.StopUpdatesAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "BLE notification stop failed (non-fatal)");
        }
    }

    /// <summary>
    /// Ensures the active BLE connection is still alive and usable. iOS can
    /// silently drop the link while the app is in the background (screen off,
    /// phone in the pocket) – the UI still shows "Connected", but writes time
    /// out. Call this when the app returns to the foreground and before
    /// sending navigation frames; it reconnects when the link is gone.
    /// </summary>
    public Task<bool> EnsureConnectedAsync()
    {
        if (_activeIDevice == null)
            return Task.FromResult(false);

        if (_activeIDevice.State == DeviceState.Connected)
            return Task.FromResult(true);

        Log.Warning("BLE link lost (status={Status}) – reconnecting", _activeIDevice.State);
        return RetryConnectionAsync(_activeIDevice.Id);
    }

    /// <summary>
    /// Marks the current connection as broken and schedules a reconnect.
    /// Used when a write times out (Arg_TimeoutException) – the classic sign
    /// that iOS dropped the link without firing a disconnect event.
    /// </summary>
    public void InvalidateConnectionAndReconnect()
    {
        IDevice? lost = _activeIDevice;
        if (lost == null) return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastInvalidateAt < InvalidateCooldown)
        {
            Log.Debug("InvalidateConnectionAndReconnect: cooldown active – skipping");
            return;
        }

        _lastInvalidateAt = now;
        Log.Warning("Forcing connection state to Idle after write failure");

        _ = StopNotificationsAsync();
        _activeIDevice = null;
        _activePlugin = null;
        _state.OnNext(BleConnectionState.Idle);
        UpdateDeviceList();
        UpdateError("Connection lost. Attempting to reconnect...");

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            try
            {
                await RetryConnectionAsync(lost.Id);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Reconnect after write failure failed");
            }
        });
    }

    // ── Plugin API Proxy
    // ── Plugin API Proxy ──────────────────────────────────────────────────

    public async Task SendTestFrameAsync()
    {
        if (_activeIDevice == null || _activePlugin == null)
        {
            UpdateError("No connected device or compatible plugin available.");
            return;
        }

        try
        {
            BleConnectedDeviceWrapper wrapper = new(_activeIDevice, _activePlugin);
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
    /// reroute (RENAVI off-route alert, early injection at ~t+02:00 so the
    /// post-RENAVI stall window is reached fast) → city again → destination.
    /// Sends NAVI+SM at the baseline 1 Hz cadence, starts the PING keepalive
    /// via the navigation-start flow (like a real ride), and stops it on
    /// FINISH. Every tick measures how long the write path took to accept
    /// the frame ("drain time"): with Plugin.BLE 5.7.2+ a clogged W2R queue shows
    /// up as ~4 s timeouts, so any tick above the 500 ms threshold or a
    /// failed write is logged as a W2R-SIM anomaly and 3+ consecutive
    /// anomalies are flagged as a detected stall. Heartbeats, segment
    /// changes and the final summary keep the exported log readable.
    /// Designed for a display-off run: no UI dependency, progress and
    /// summary are logged under "W2R-SIM" for later analysis.
    /// </summary>
    public async Task<W2rRouteSimResult> RunW2rRouteSimAsync(double? startLat, double? startLon, CancellationToken ct, bool withReroute = true)
    {
        if (_activeIDevice is null || _activePlugin is null)
            return new W2rRouteSimResult(0, 0, 0, 0, null, "W2R-SIM: no connected device");

        BleConnectedDeviceWrapper wrapper = new(_activeIDevice, _activePlugin);
        IDevice? wrapperIDevice = _activeIDevice;
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

            // Early off-route injection at t+02:00 (was: city-2 segment start at
            // t+20:00) so the post-RENAVI stall window is reached in ~2:46
            // instead of ~20:46 – the test no longer requires waiting 20 min.
            const int rerouteAtSecond = 120;

            for (int mi = 0; mi < profile.Count; mi++)
            {
                SimManeuver m = profile[mi];

                // Simulated reroute: off-route alert + remaining distance bump,
                // injected early (t+02:00) to reach the stall window fast.
                // withReroute=false = control run (Kontrolllauf) – same profile,
                // no off-route alert, to test whether the stall is REROUTE-triggered.
                if (second >= rerouteAtSecond && !rerouteSent)
                {
                    rerouteSent = true;
                    if (withReroute)
                    {
                        remainingM += 1600; // the reroute adds 1.6 km
                        if (_activePlugin is not null)
                        {
                            Log.Information("BLE-LOGGER: {Line}",
                                $"W2R-SIM REROUTE t+{FormatSimTm(second)}: RENAVI sent (early injection, simulated off-route), remaining +1.6 km");
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
                            $"W2R-SIM CONTROL t+{FormatSimTm(second)}: Reroute deaktiviert (Kontrolllauf) – kein Off-Route-Alert");
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
                    // reassigns _activeIDevice/_activePlugin mid-run. While
                    // the link is down, skip the write (counted as a failed
                    // tick); when a new connection appears, rebuild the
                    // wrapper so ticks keep flowing on the live link instead
                    // of NRE-ing on the dead objects (see run 20260927, tick 1259+).
                    bool linkDown = _activeIDevice is null || _activePlugin is null;
                    if (linkDown)
                    {
                        Log.Information("BLE-LOGGER: {Line}",
                            $"W2R-SIM LINK DOWN t+{FormatSimTm(second)} seg={m.Segment} tick={ticks} – write skipped (reconnect in progress)");
                    }
                    else if (!ReferenceEquals(wrapperIDevice, _activeIDevice) || !ReferenceEquals(wrapperPlugin, _activePlugin))
                    {
                        wrapper = new BleConnectedDeviceWrapper(_activeIDevice, _activePlugin);
                        wrapperIDevice = _activeIDevice;
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

    /// <summary>
    /// Rescan-level reset test (middle level of the reset hierarchy – untested
    /// until now). PRE: 3 NAVI test instructions on the current connection with
    /// per-write drain measurement. RESET: full teardown (DisconnectAsync) + fresh
    /// rescan + new IDevice object + reconnect. POST: 3 instructions on the
    /// new connection. If a clogged/stuck state lives in the old GATT session,
    /// PRE shows the stall (~3 s / timeout) and POST returns to baseline (~300 ms).
    /// Everything is logged under "W2R-RR"; the returned line is the UI caption.
    /// </summary>
    public async Task<string> RunRescanResetTestAsync(CancellationToken ct = default)
    {
        IDevice? oldIDevice = _activeIDevice;
        IBleDevicePlugin? oldPlugin = _activePlugin;
        if (oldIDevice is null || oldPlugin is null)
            return "W2R-RR: nicht gestartet – keine aktive Verbindung";
        if (oldPlugin is not MvAgustaBlePlugin)
            return "W2R-RR: nicht gestartet – aktives Plugin ist kein MV-Agusta-Plugin";

        Guid uuid = Guid.Parse(oldIDevice.Id);
        string uuidKey = uuid.ToString().ToUpper();
        string oldRef = RefHash(oldIDevice);

        NavigationUpdateInput testInput = new()
        {
            ManeuverIcon = "turn-right",
            InstructionText = "W2R-RR: rechts abbiegen",
            StreetName = "W2R-RR",
            DistanceToTurnM = 200,
            SpeedKmh = 30,
            RemainingDistanceKm = 10.0,
            RemainingTimeMin = 20.0,
            CurrentManeuverIndex = 0,
            TotalManeuvers = 3,
            IsFinal = false
        };

        async Task<(bool ok, int drainMs)> WriteOneAsync(IBleDevicePlugin plugin, BleConnectedDeviceWrapper wrapper, string phase, int idx)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok;
            try
            {
                await plugin.SendNavigationUpdateAsync(wrapper, testInput);
                ok = true;
            }
            catch (Exception ex)
            {
                ok = false;
                Log.Warning("W2R-RR {Phase} #{Idx}: write failed – {Ex}", phase, idx, ex.Message);
            }
            int drainMs = (int)sw.ElapsedMilliseconds;
            Log.Information("BLE-LOGGER: {Line}", $"W2R-RR {phase} #{idx}: {(ok ? "OK" : "FAIL")} drain {drainMs} ms");
            return (ok, drainMs);
        }

        // ── PRE: 3 instructions on the current (possibly clogged) connection
        int preOk = 0, preMax = 0;
        Log.Information("BLE-LOGGER: {Line}", $"W2R-RR PRE: 3 test instructions on the current connection (peripheral {oldRef})");
        var preWrapper = new BleConnectedDeviceWrapper(oldIDevice, oldPlugin);
        for (int i = 1; i <= 3; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (ok, drain) = await WriteOneAsync(oldPlugin, preWrapper, "PRE", i);
            preOk += ok ? 1 : 0;
            preMax = Math.Max(preMax, drain);
            if (i < 3)
                await Task.Delay(500, ct);
        }

        // ── RESET: full teardown + fresh rescan + new peripheral object + reconnect
        Log.Information("BLE-LOGGER: {Line}", $"W2R-RR RESET: tearing down old connection + fresh rescan of {uuid}");
        var resetSw = System.Diagnostics.Stopwatch.StartNew();
        await DisconnectAsync();
        StopScanning();
        var _ = StartScanningAsync(); // runs 30 s in the background – we stop it early

        // Poll the rescan cache until the bike re-appears (up to 15 s).
        bool found = false;
        for (int i = 0; i < 30 && !found; i++)
        {
            ct.ThrowIfCancellationRequested();
            found = _discoveredIDevices.ContainsKey(uuidKey);
            if (!found)
                await Task.Delay(500, ct);
        }
        if (!found)
        {
            StopScanning();
            string fail = $"W2R-RR RESET FAILED: {uuid} did not re-appear in the rescan (15 s) – connection is now down";
            Log.Warning("BLE-LOGGER: {Line}", fail);
            return fail;
        }

        bool connected = await ConnectAsync(uuid);
        StopScanning();
        long resetMs = resetSw.ElapsedMilliseconds;
        if (!connected)
        {
            string fail = $"W2R-RR RESET FAILED: reconnect of {uuid} after rescan failed (took {resetMs} ms)";
            Log.Warning("BLE-LOGGER: {Line}", fail);
            return fail;
        }
        if (_activeIDevice is null || _activePlugin is not MvAgustaBlePlugin)
        {
            string fail = "W2R-RR RESET FAILED: no MV Agusta plugin after reconnect";
            Log.Warning("BLE-LOGGER: {Line}", fail);
            return fail;
        }

        bool sameObject = ReferenceEquals(oldIDevice, _activeIDevice);
        Log.Information("BLE-LOGGER: {Line}",
            $"W2R-RR RESET: done in {resetMs} ms – peripheral object: {(sameObject ? "SAME (reused by BLE library cache)" : "NEW (fresh scan object)")}, {oldRef} -> {RefHash(_activeIDevice)}");

        // ── POST: 3 instructions on the new connection
        int postOk = 0, postMax = 0;
        Log.Information("BLE-LOGGER: {Line}", $"W2R-RR POST: 3 test instructions on the new connection");
        var postWrapper = new BleConnectedDeviceWrapper(_activeIDevice, _activePlugin);
        for (int i = 1; i <= 3; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (ok, drain) = await WriteOneAsync(_activePlugin, postWrapper, "POST", i);
            postOk += ok ? 1 : 0;
            postMax = Math.Max(postMax, drain);
            if (i < 3)
                await Task.Delay(500, ct);
        }

        string summary = $"W2R-RR DONE: PRE {preOk}/3 (max {preMax} ms) → RESET {resetMs} ms ({(sameObject ? "same" : "new")} object) → POST {postOk}/3 (max {postMax} ms)";
        Log.Information("BLE-LOGGER: {Line}", summary);
        return summary;
    }

    /// <summary>Human-readable reference id for a peripheral object (type + hashed instance).</summary>
    private static string RefHash(IDevice peripheral) =>
        $"{peripheral.GetType().Name}@{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(peripheral):X8}";

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
        if (_activeIDevice == null || _activePlugin == null)
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

            BleConnectedDeviceWrapper wrapper = new(_activeIDevice!, _activePlugin);

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
            // entire 1s tick) and don't reconnect (let Plugin.BLE's disconnect
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
        if (_activeIDevice == null || _activePlugin == null) return;

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

            BleConnectedDeviceWrapper wrapper = new(_activeIDevice!, _activePlugin);
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
        if (_activeIDevice == null || _activePlugin == null) return;

        Log.Information("BLE-LOGGER: {Line}", "NAV ACTION: SendNavigationStopAsync");

        try
        {
            // Same link-health guard as ExecuteNavigationActionAsync.
            if (!await EnsureConnectedAsync())
            {
                Log.Warning("ExecuteNavigationStopAsync: link not healthy, skipping write");
                return;
            }

            BleConnectedDeviceWrapper wrapper = new(_activeIDevice!, _activePlugin);
            await _activePlugin.SendNavigationStopAsync(wrapper);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send navigation stop");
            UpdateError($"Navigation stop failed: {ex.Message}", isCritical: false);
        }
    }

    private void RefreshConnectedIDevices()
    {
        try
        {
            foreach (IDevice connected in _adapter.GetSystemConnectedOrPairedDevices())
            {
                string key = connected.Id.ToString().ToUpperInvariant();
                _discoveredIDevices[key] = connected;
            }

            UpdateDeviceList();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "BLE: RefreshConnectedIDevices failed (non-fatal)");
        }
    }

    private async Task SetupNotificationsAsync(IDevice peripheral)
    {
        if (_activePlugin == null) return;

        Log.Information(
            "Setting up notifications for {Uuid} using Plugin.BLE plugin {Plugin}",
            peripheral.Id,
            _activePlugin.DisplayName);

        IService service = await peripheral.GetServiceAsync(_activePlugin.ServiceUuid);
        ICharacteristic characteristic =
            await service.GetCharacteristicAsync(Guid.Parse(_activePlugin.ReadCharacteristicUuid));

        EventHandler<CharacteristicUpdatedEventArgs> handler = (_, args) =>
        {
            byte[] data = args.Characteristic.Value;
            if (data is { Length: > 0 })
                _activePlugin?.OnDataReceived(data);
        };

        characteristic.ValueUpdated += handler;
        _notificationCharacteristic = characteristic;
        _notificationHandler = handler;

        try
        {
            await characteristic.StartUpdatesAsync();

            Log.Information(
                "BLE notifications enabled: {Service}/{Characteristic}",
                _activePlugin.ServiceUuid,
                _activePlugin.ReadCharacteristicUuid);
        }
        catch
        {
            characteristic.ValueUpdated -= handler;
            _notificationCharacteristic = null;
            _notificationHandler = null;
            throw;
        }
    }

    private void HandleUnexpectedDisconnection(IDevice peripheral)
    {
        if (_activeIDevice == null || _activeIDevice.Id != peripheral.Id)
            return;

        if (_intentionalDisconnectId == peripheral.Id)
            return;

        Log.Warning("Unexpected BLE disconnection detected for {Uuid}", peripheral.Id);
        _state.OnNext(BleConnectionState.Idle);
        UpdateDeviceList();
        UpdateError("Connection lost. Attempting to reconnect...");

        _ = Task.Run(async () =>
        {
            try
            {
                await RetryConnectionAsync(peripheral.Id);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Reconnect after unexpected disconnection failed");
            }
        });
    }

    private void OnAdapterDeviceDisconnected(object? sender, DeviceEventArgs e)
        => HandleUnexpectedDisconnection(e.Device);

    private void OnAdapterDeviceConnectionLost(object? sender, DeviceErrorEventArgs e)
        => HandleUnexpectedDisconnection(e.Device);

    private void OnAdapterDeviceConnectionError(object? sender, DeviceErrorEventArgs e)
    {
        if (_activeIDevice?.Id == e.Device.Id)
        {
            Log.Warning("BLE connection error for {Uuid}: {Error}", e.Device.Id, e.ErrorMessage);
            HandleUnexpectedDisconnection(e.Device);
        }
    }

    private void OnDeviceDiscovered(object? sender, DeviceEventArgs e)
    {
        _discoveredIDevices[e.Device.Id.ToString().ToUpperInvariant()] = e.Device;
        UpdateDeviceList();
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

                IDevice device = await _adapter.ConnectToKnownDeviceAsync(
                    deviceUuid,
                    cancellationToken: token);

                _discoveredIDevices[device.Id.ToString().ToUpperInvariant()] = device;

                if (await CompleteConnectionAsync(device))
                    return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Retry attempt {Attempt} failed for {Uuid}", attempt, deviceUuid);
            }
        }

        if (!token.IsCancellationRequested)
            UpdateError("Connection lost. Reconnection attempts failed.");

        return false;
    }

    private void UpdateDeviceFromScanResult(IDevice result)
    {
        _discoveredIDevices[result.Id.ToString().ToUpperInvariant()] = result;
        UpdateDeviceList();
    }

    private void UpdateDeviceList()
    {
        List<BleDeviceInfo> list =
        [
            .. _discoveredIDevices.Values
                .Where(p =>
                    (!string.IsNullOrWhiteSpace(p.Name) && p.Name != "Unknown") ||
                    p.State == DeviceState.Connected)
                .Select(p =>
                {
                    BleDeviceInfo deviceInfo = new()
                    {
                        Uuid = p.Id,
                        Name = string.IsNullOrWhiteSpace(p.Name) ? "Unknown" : p.Name,
                        IsConnected = p.State == DeviceState.Connected,
                        LastSeen = DateTime.Now
                    };

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
        _ = StopNotificationsAsync();

        _adapter.DeviceDiscovered -= OnDeviceDiscovered;
        _adapter.DeviceDisconnected -= OnAdapterDeviceDisconnected;
        _adapter.DeviceConnectionLost -= OnAdapterDeviceConnectionLost;
        _adapter.DeviceConnectionError -= OnAdapterDeviceConnectionError;

        _retryCts?.Cancel();
        _retryCts?.Dispose();
        _state.Dispose();
        _devices.Dispose();
        _errorMessage.Dispose();
    }

    // ── HAL Implementation ────────────────────────────────────────────────

    private sealed class BleConnectedDeviceWrapper(IDevice peripheral, IBleDevicePlugin plugin) : IBleConnectedDevice
    {
        // Plugin.BLE recommends serial BLE operations. Keep writes ordered so
        // navigation frames and keepalive frames never overlap.
        private static readonly SemaphoreSlim WriteGate = new(1, 1);

        public Guid Uuid => peripheral.Id;
        public string Name => peripheral.Name ?? "Unknown";

        public async Task WriteAsync(string characteristicUuid, byte[] data, bool withResponse)
        {
            Guid serviceUuid = plugin.ServiceUuid;
            Guid characteristicId = Guid.Parse(characteristicUuid);

            await WriteGate.WaitAsync();
            try
            {
                IService service = await peripheral.GetServiceAsync(serviceUuid);
                ICharacteristic characteristic =
                    await service.GetCharacteristicAsync(characteristicId);

                characteristic.WriteType = withResponse
                    ? CharacteristicWriteType.WithResponse
                    : CharacteristicWriteType.WithoutResponse;

                Stopwatch stopwatch = Stopwatch.StartNew();
                int result = await MainThread.InvokeOnMainThreadAsync(
                    () => characteristic.WriteAsync(data));
                stopwatch.Stop();

                Log.Debug(
                    "Plugin.BLE write {Type}: {Service}/{Characteristic}, {Bytes} bytes, result={Result}, elapsed={ElapsedMs}ms",
                    withResponse ? "WWR" : "W2R",
                    serviceUuid,
                    characteristicId,
                    data.Length,
                    result,
                    stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"Failed to write to characteristic {characteristicId} on service {serviceUuid}: {ex.Message}",
                    ex);
            }
            finally
            {
                WriteGate.Release();
            }
        }

        public async Task<byte[]?> ReadAsync(string characteristicUuid)
        {
            Guid serviceUuid = plugin.ServiceUuid;
            Guid characteristicId = Guid.Parse(characteristicUuid);

            try
            {
                IService service = await peripheral.GetServiceAsync(serviceUuid);
                ICharacteristic characteristic =
                    await service.GetCharacteristicAsync(characteristicId);

                (byte[] data, int resultCode) = await characteristic.ReadAsync();
                _ = resultCode;
                return data;
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"Failed to read characteristic {characteristicId} on service {serviceUuid}: {ex.Message}",
                    ex);
            }
        }
    }
}
