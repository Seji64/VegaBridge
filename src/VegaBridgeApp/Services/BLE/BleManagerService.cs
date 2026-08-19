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
                                UpdateDeviceList();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "BLE: GetConnectedPeripherals poll failed (non-fatal)");
                    }
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
                Log.Debug("BLE connection still alive (EnsureConnected)");
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

    public async Task SendCommandAsync(string command, params string[] fields)
    {
        if (_activePeripheral == null || _activePlugin == null)
        {
            UpdateError("No connected device or compatible plugin available.");
            return;
        }

        try
        {
            BleConnectedDeviceWrapper wrapper = new(_activePeripheral, _activePlugin);
            Log.Information("BLE-LOGGER: {Line}", $"SEND CMD: {command} fields=[{string.Join(", ", fields)}]");
            await _activePlugin.SendAsync(wrapper, command, fields);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send command {Command}", command);
            UpdateError($"Command failed: {ex.Message}");
        }
    }

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
            Log.Warning(ex, "Write failed for {Action}, retrying in 500ms", action);

            // Retry once after a short delay. The BLE write queue might be
            // full (CanSendWriteWithoutResponse = False). A brief wait lets
            // the queue drain.
            try
            {
                await Task.Delay(500);
                if (_activePeripheral == null || _activePlugin == null) return;
                BleConnectedDeviceWrapper retryWrapper = new(_activePeripheral, _activePlugin);
                switch (action)
                {
                    case "SendNavigationUpdateAsync":
                        if (input is NavigationUpdateInput u)
                            await _activePlugin.SendNavigationUpdateAsync(retryWrapper, u);
                        break;
                    case "SendOffRouteAlertAsync":
                        if (input is OffRouteAlertInput a)
                            await _activePlugin.SendOffRouteAlertAsync(retryWrapper, a);
                        break;
                }
                Log.Information("Retry succeeded for {Action}", action);
                return;
            }
            catch (Exception retryEx)
            {
                Log.Warning(retryEx, "Retry also failed for {Action} – will recover on next tick", action);
            }

            // Don't reconnect here. Let Shiny's WhenDisconnected() /
            // WhenConnectionFailed() handle actual connection loss.
            // A write failure might just mean the queue is full – the
            // connection could still be alive. Next tick will try again.
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
