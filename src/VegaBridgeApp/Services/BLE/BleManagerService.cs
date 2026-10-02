using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Serilog;
using Shiny.BluetoothLE;
using VegaBridgeApp.Models.BLE;
using VegaBridgeApp.Services.BLE.Plugins;
using VegaBridgeApp.Services.Debug;

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

    // Maintain our own dictionary of discovered peripherals for reliable access
    private readonly ConcurrentDictionary<string, IPeripheral> _discoveredPeripherals = new();
    
    private readonly IEnumerable<IBleDevicePlugin> _plugins = plugins;

    // Intent flag (docs: "A deliberate disconnect stays disconnected; call
    // Connect() again to re-arm"): DisconnectAsync() sets it, ConnectAsync()
    // clears it. While set, no path may auto-reconnect – otherwise a
    // user-initiated disconnect gets overridden on the next app resume.
    private volatile bool _userInitiatedDisconnect;

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

            _scanTimeoutCts = new CancellationTokenSource();
            CancellationToken scanToken = _scanTimeoutCts.Token;

            // Peripherals connected in the background (iOS state restoration)
            // often stop advertising, so a pure scan never sees them. They
            // surface via GetConnectedPeripherals() – one-shot here, and
            // continuously via the delegate's OnAdapterStateChanged.
            RefreshConnectedPeripherals();

            // Deliberately unfiltered: the MV Agusta display advertises by
            // NAME, not by its 128-bit service UUID – and iOS only matches a
            // service-UUID filter against what the peripheral puts into its
            // advertising payload, so a filtered scan here finds nothing
            // (verified on-device: empty device list). The Shiny docs
            // require a ServiceUuids filter for iOS *background* scanning
            // only; we scan in the foreground, so unfiltered is correct.
            Log.Information("BLE scanning started (unfiltered)");
            _scanSubscription = bleManager
                .ScanForUniquePeripherals()
                .Subscribe(UpdateDeviceFromScanResult);
            
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

            // No explicit ConnectionConfig: Shiny 5.7.2 defaults a null config
            // to AutoConnect = true, so Shiny owns link recovery (dropped
            // links, adapter power cycles ≥ 5.6). Per the docs we must NOT
            // run our own WhenDisconnected→Connect loop on top of it.
            await peripheral.ConnectAsync(timeout: TimeSpan.FromSeconds(30));

            _userInitiatedDisconnect = false;
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
            
            SetupWhenConnected(peripheral);
            SetupNotifications(peripheral);

            // No keepalive re-arm needed on (re)connect: the navigation
            // coordinator's 15 s PING tick picks up the new link on its
            // own (ExecuteKeepAliveAsync resolves the current connection).

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

            // Deliberate disconnect: Shiny's DisconnectAsync() extension
            // calls CancelConnection() internally (5.7.2), which disposes
            // the peripheral's auto-reconnect. The intent flag makes that
            // stick – no resume/send path may silently reconnect.
            _userInitiatedDisconnect = true;
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
    /// Ensures the active BLE connection is still alive and usable. Call this
    /// when the app returns to the foreground and before sending navigation
    /// frames. Docs (Shiny.BluetoothLE ≥ 5.6): with AutoConnect enabled,
    /// Shiny owns ALL link recovery – we must not issue competing connects
    /// ("the two fight each other"). The auto-reconnect is armed by
    /// ConnectAsync (default) and only disarmed by a user-initiated
    /// DisconnectAsync (which calls Shiny's CancelConnection internally),
    /// so every non-user link loss is Shiny's responsibility; our
    /// WhenConnected hook (SetupWhenConnected) re-runs the per-connection
    /// setup as soon as the link comes back. This method therefore never
    /// triggers a reconnect itself.
    /// </summary>
    public Task<bool> EnsureConnectedAsync()
    {
        IPeripheral? peripheral = _activePeripheral;
        if (peripheral == null)
            return Task.FromResult(false);

        // Shiny reports the live link status; Connected means the GATT link
        // is really alive.
        if (peripheral.Status == ConnectionState.Connected)
            return Task.FromResult(true);

        // Deliberate disconnect: stay disconnected until the user connects
        // again via the UI (docs: a deliberate disconnect stays
        // disconnected, Connect() re-arms the auto-reconnect).
        if (_userInitiatedDisconnect)
        {
            Log.Information("BLE: user-initiated disconnect – not auto-reconnecting");
            return Task.FromResult(false);
        }

        // Non-intent link loss: Shiny's auto-reconnect owns the recovery.
        // Report "not usable" so the caller skips this write; the next tick
        // retries, and WhenConnected re-runs our setup when the link is
        // back. Never a home-grown retry loop (docs).
        Log.Information("BLE link down (status={Status}) – waiting for Shiny auto-reconnect", peripheral.Status);
        return Task.FromResult(false);
    }

    /// <summary>
    /// Forwarded by <see cref="VegaBridgeBleDelegate"/> (Apple targets only):
    /// adapter-level state changes (Bluetooth toggled in system settings, app
    /// restarted by state restoration) arrive here reliably, because
    /// observable subscriptions do not survive app sleep/restart cycles.
    /// Docs (Shiny.BluetoothLE, 5.6+): Shiny owns the adapter cycle – it tears
    /// down connected peripherals on power-down and reconnects every
    /// AutoConnect-armed peripheral on power-up. This handler must therefore
    /// NEVER issue Connect()/Scan(); it only keeps our state and UI in sync.
    /// </summary>
    public void OnBleAdapterStateChanged(AccessState state)
    {
        Log.Information("BLE adapter state changed: {State}", state);

        if (state != AccessState.Available)
        {
            // Bluetooth is off: Shiny runs the full disconnect teardown on
            // every connected peripheral (WhenStatusChanged emits Disconnected
            // in the foreground). If we still hold a live reference, mark the
            // link lost so the UI does not look like it is still connected.
            IPeripheral? peripheral = _activePeripheral;
            if (peripheral is not null && peripheral.Status != ConnectionState.Disconnected)
            {
                _state.OnNext(BleConnectionState.Idle);
                if (!_userInitiatedDisconnect)
                    UpdateError("Bluetooth is off – the connection will be restored automatically once it is back on.", isCritical: false);
                UpdateDeviceList();
            }
            return;
        }

        // Adapter back on: Shiny reconnects the AutoConnect-armed peripherals
        // on its own. Surface OS-connected devices even when our subscriptions
        // are dead (e.g. the app was restarted in the background via state
        // restoration and BleManagerService is a fresh instance). The error
        // banner is cleared by OnLinkRestored() once the link is really up.
        RefreshConnectedPeripherals();
        UpdateDeviceList();
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


    // ── Accessors for the W2R route-sim (Services/BLE/W2rRouteSim.cs) ────

    /// <summary>Current live link pair; null while no connection.</summary>
    public (IPeripheral Peripheral, IBleDevicePlugin Plugin)? ActiveLink
        => _activePeripheral is { } peripheral && _activePlugin is { } plugin
            ? (peripheral, plugin)
            : null;

    public bool IsUserInitiatedDisconnect => _userInitiatedDisconnect;

    /// <summary>Builds a write wrapper around a specific live link pair.</summary>
    public IBleConnectedDevice CreateConnectedDevice(IPeripheral peripheral, IBleDevicePlugin plugin)
        => new BleConnectedDeviceWrapper(peripheral, plugin);


    /// <summary>
    /// Guard + link-health check + write wrapper shared by all frame writes:
    /// no-op when no active device/plugin. The classic "stuck navigation"
    /// failure mode: iOS drops the link while the phone is in the pocket
    /// (screen off, app suspended) but no disconnect event arrives – writing
    /// into a dead link blocks for the write timeout (~10 s+) and every
    /// subsequent update queues behind it. The link is verified first.
    /// Write failures are not retried (a 500 ms block stalls the entire 1 s
    /// tick); the next tick/GPS update resends a fresh frame, and Shiny's
    /// auto-reconnect owns link recovery.
    /// Returns whether the frame was actually delivered.
    /// </summary>
    private async Task<bool> WithLiveLinkAsync(string action, Func<IBleConnectedDevice, Task> send, bool critical = false)
    {
        if (_activePeripheral is not { } peripheral || _activePlugin is not { } plugin)
        {
            Log.Debug("{Action} skipped: no active device/plugin", action);
            return false;
        }

        Log.Information("BLE-LOGGER: {Line}", $"NAV ACTION: {action}");

        try
        {
            if (!await EnsureConnectedAsync())
            {
                Log.Warning("{Action}: link not healthy, skipping write", action);
                UpdateError("Connection lost. Reconnection attempts failed.", isCritical: false);
                return false;
            }

            await send(new BleConnectedDeviceWrapper(peripheral, plugin));
            return true;
        }
        catch (Exception ex)
        {
            if (critical)
            {
                Log.Error(ex, "BLE write failed: {Action}", action);
                UpdateError($"BLE write failed: {ex.Message}", isCritical: false);
            }
            else
            {
                Log.Debug(ex, "Write failed for {Action} – next tick will retry", action);
            }
            return false;
        }
    }

    /// <summary>
    /// Sends the navigation-start frame (DEST + REM) through the active plugin.
    /// </summary>
    public async Task ExecuteNavigationStartAsync(NavigationStartInput input)
    {
        await WithLiveLinkAsync("SendNavigationStartAsync", d => _activePlugin!.SendNavigationStartAsync(d, input));
    }

    /// <summary>
    /// Sends a navigation-update frame through the active plugin.
    /// Returns whether the frame was actually delivered to the device –
    /// false when skipped (no device), the link is unhealthy, or the
    /// write failed. Callers use this to decide whether a NAVI write must
    /// be re-requested.
    /// </summary>
    public Task<bool> ExecuteNavigationUpdateAsync(NavigationUpdateInput input, bool sendNavi = true)
        => WithLiveLinkAsync("SendNavigationUpdateAsync", d => _activePlugin!.SendNavigationUpdateAsync(d, input, sendNavi));

    /// <summary>
    /// Sends an off-route alert (RENAVI) through the active plugin.
    /// </summary>
    public async Task ExecuteNavigationOffRouteAlertAsync()
    {
        await WithLiveLinkAsync("SendOffRouteAlertAsync", d => _activePlugin!.SendOffRouteAlertAsync(d));
    }

    /// <summary>
    /// Handles destination reached via the active plugin.
    /// </summary>
    public async Task ExecuteNavigationFinishAsync()
    {
        await WithLiveLinkAsync("SendNavigationFinishAsync", d => _activePlugin!.SendNavigationFinishAsync(d), critical: true);
    }

    /// <summary>
    /// Sends the manufacturer's keepalive frame (MV Agusta: PING) through
    /// the active plugin. The 15 s cadence and the 5 s post-NAVI skip are
    /// owned by the navigation coordinator – this method is one keepalive
    /// send against the current live link, no-op when no connection exists.
    /// </summary>
    public async Task ExecuteKeepAliveAsync()
    {
        await WithLiveLinkAsync("SendKeepAliveAsync", d => _activePlugin!.SendKeepAliveAsync(d));
    }

    /// <summary>
    /// Handles user-cancelled navigation via the active plugin.
    /// </summary>
    public async Task ExecuteNavigationStopAsync()
    {
        await WithLiveLinkAsync("SendNavigationStopAsync", d => _activePlugin!.SendNavigationStopAsync(d), critical: true);
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

    // ── Connection Monitoring (Shiny owns reconnection) ───────────────────

    /// <summary>
    /// Docs (Shiny.BluetoothLE, ≥ 5.6): with AutoConnect enabled, Shiny
    /// reconnects dropped links AND adapter power cycles by itself. Our job
    /// is NOT to drive reconnects – it is to re-run everything we did at
    /// connect time from WhenConnected() ("Auto-reconnect restores the link,
    /// not your GATT state. … Hook WhenConnected() and do that work there
    /// rather than after the first ConnectAsync()").
    /// Status/failure observables are UI/log signals only.
    /// </summary>
    private void SetupWhenConnected(IPeripheral peripheral)
    {
        _connectionSubscription?.Dispose();

        // Docs: re-run per-connection setup whenever the (auto-)reconnected
        // link comes back. This is the ONLY reconnect hook we own.
        IObservable<IPeripheral> connected = peripheral.WhenConnected();
        var connectedSub = connected.Subscribe(_ => OnLinkRestored(peripheral));

        // UI state tracking only – never a reconnect trigger (docs: a home-grown
        // WhenDisconnected→Connect loop fights Shiny's auto-reconnect).
        var statusSub = peripheral.WhenStatusChanged()
            .Subscribe(status =>
            {
                if (!ReferenceEquals(_activePeripheral, peripheral)) return;
                if (status == ConnectionState.Disconnected)
                {
                    // Log-only (timeline marker) – Shiny's AutoConnect owns the
                    // actual reconnection, we never act on this event.
                    Log.Information("BLE-LOGGER: {Line}",
                        $"BLE peripheral {peripheral.Uuid} status -> Disconnected{(_userInitiatedDisconnect ? " (user-initiated)" : " – Shiny auto-reconnect in progress")}");
                    _state.OnNext(BleConnectionState.Idle);
                    if (!_userInitiatedDisconnect)
                    {
                        UpdateError("Connection lost – reconnecting (automatic).", isCritical: false);
                    }
                    UpdateDeviceList();
                }
            });

        // In-flight operations fault with BleException on a dead link; Shiny's
        // auto-reconnect retries once the peripheral reports Connected again.
        // We only log failures – no reconnect logic here.
        var failSub = peripheral.WhenConnectionFailed()
            .Subscribe(ex =>
            {
                Log.Warning("Connection failed for {Uuid}: {Error}", peripheral.Uuid, ex.Message);
            });

        _connectionSubscription = new System.Reactive.Disposables.CompositeDisposable(connectedSub, statusSub, failSub);
    }

    /// <summary>
    /// Fires when Shiny (re)established the GATT link – initial auto-reconnect
    /// after a dropped link, adapter power-cycle recovery, or state
    /// restoration. Re-runs the per-connection setup exactly like
    /// ConnectAsync() does, because auto-reconnect restores the link but NOT
    /// our GATT/plugin state (docs).
    /// </summary>
    private void OnLinkRestored(IPeripheral peripheral)
    {
        // The user may have disconnected this peripheral in the meantime –
        // only re-arm setup for the device we actually track.
        if (!ReferenceEquals(_activePeripheral, peripheral)) return;

        Log.Information("BLE link (re)established for {Uuid} – re-running per-connection setup", peripheral.Uuid);
        _state.OnNext(BleConnectionState.Connected);
        _errorMessage.OnNext(string.Empty);

        // Plugin may still be null after a background drop (or the name was
        // only readable now) – re-select like in ConnectAsync().
        if (_activePlugin == null)
        {
            _activePlugin = SelectActivePlugin(peripheral);
        }

        SetupNotifications(peripheral);
        // No keepalive re-arm needed: the navigation coordinator's 15 s PING
        // tick resolves the current connection itself (ExecuteKeepAliveAsync).
        UpdateDeviceList();
    }

    /// <summary>
    /// Synchronous name-based plugin re-match for the WhenConnected hook.
    /// The GATT service-UUID fallback stays in ConnectAsync, where a GATT
    /// read is affordable – the hook must not block on one (and a
    /// fire-and-forget fallback would write _activePlugin from a background
    /// task racing the UI thread).
    /// </summary>
    private IBleDevicePlugin? SelectActivePlugin(IPeripheral peripheral)
    {
        BleDeviceInfo deviceInfo = new() { Uuid = Guid.Parse(peripheral.Uuid), Name = peripheral.Name ?? "Unknown" };
        IBleDevicePlugin? match = _plugins.FirstOrDefault(p => p.IsCompatible(deviceInfo));
        if (match != null)
            return match;

        Log.Warning("BLE: no name-based plugin match on link restore for {Uuid} ({Name}) – notifications stay unsubscribed until manual reconnect",
            peripheral.Uuid, peripheral.Name ?? "Unknown");
        return null;
    }

    private void SetupNotifications(IPeripheral peripheral)
    {
        _notificationSubscription?.Dispose();

        if (_activePlugin == null) return;

        Log.Information("Setting up notifications for {Uuid} using plugin {Plugin}", peripheral.Uuid, _activePlugin.DisplayName);

        // Docs: NotifyCharacteristic re-subscribes itself on auto-reconnect
        // as long as this observable subscription is still alive – so we
        // only re-run it after a user-initiated disconnect (Dispose above).
        _notificationSubscription = peripheral.NotifyCharacteristic(
                _activePlugin.ServiceUuid.ToString(),
                _activePlugin.ReadCharacteristicUuid)
            .Subscribe(result =>
            {
                if (result.Data != null) _activePlugin.OnDataReceived(result.Data);
            });
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
        _state.Dispose();
        _devices.Dispose();
        _errorMessage.Dispose();
    }

    // ── HAL Implementation ────────────────────────────────────────────────

    private class BleConnectedDeviceWrapper(IPeripheral peripheral, IBleDevicePlugin plugin) : IBleConnectedDevice
    {
        public Guid Uuid => Guid.Parse(peripheral.Uuid);
        public string Name => peripheral.Name ?? "Unknown";

        // No exception wrapping: Shiny's own exceptions (BleException etc.)
        // propagate as-is; callers catch and log.
        public async Task WriteAsync(string characteristicUuid, byte[] data, bool withResponse)
        {
            string serviceUuid = plugin.ServiceUuid.ToString();
            await peripheral.WriteCharacteristicAsync(serviceUuid, characteristicUuid, data, withResponse);
        }

        public async Task<byte[]?> ReadAsync(string characteristicUuid)
        {
            string serviceUuid = plugin.ServiceUuid.ToString();
            BleCharacteristicResult result = await peripheral.ReadCharacteristicAsync(serviceUuid, characteristicUuid);
            return result.Data;
        }
    }
}
