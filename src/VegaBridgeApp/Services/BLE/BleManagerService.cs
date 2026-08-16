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

            Log.Information("BLE scanning started");
            
            _scanSubscription = bleManager.ScanForUniquePeripherals().Subscribe(UpdateDeviceFromScanResult);
            
            _scanTimeoutCts = new CancellationTokenSource();
            
            await Task.Delay(TimeSpan.FromSeconds(30), _scanTimeoutCts.Token);
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
            
            // Plugin Selection
            BleDeviceInfo deviceInfo = new() { Uuid = deviceUuid, Name = peripheral.Name! };
            _activePlugin = _plugins.FirstOrDefault(p => p.IsCompatible(deviceInfo));

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

        try
        {
            BleConnectedDeviceWrapper wrapper = new(_activePeripheral, _activePlugin);

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
            Log.Error(ex, "Failed to execute navigation action {Action}", action);
            UpdateError($"Navigation command failed: {ex.Message}", isCritical: false);

            // Write timeouts (Arg_TimeoutException) are the classic sign that
            // iOS dropped the BLE link while the app was in the background –
            // without a disconnect event. Rebuild the connection instead of
            // silently failing every subsequent frame.
            if (_activePeripheral != null)
            {
                InvalidateConnectionAndReconnect();
            }
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
            BleConnectedDeviceWrapper wrapper = new(_activePeripheral, _activePlugin);
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
            BleConnectedDeviceWrapper wrapper = new(_activePeripheral, _activePlugin);
            await _activePlugin.SendNavigationStopAsync(wrapper);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send navigation stop");
            UpdateError($"Navigation stop failed: {ex.Message}", isCritical: false);
        }
    }

    // ── Connection Monitoring & Retry ─────────────────────────────────────

    private void SetupConnectionMonitoring(IPeripheral peripheral)
    {
        _connectionSubscription?.Dispose();
        _connectionSubscription = peripheral.WhenDisconnected()
            .Subscribe(_ => HandleUnexpectedDisconnection(peripheral));
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
                .Where(p => !string.IsNullOrWhiteSpace(p.Name) && p.Name != "Unknown")
                .Select(p => 
                {
                    BleDeviceInfo deviceInfo = new()
                    {
                        Uuid = Guid.Parse(p.Uuid),
                        Name = p.Name!,
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
