using System.Reactive.Linq;
using System.Reactive.Subjects;
using Serilog;
using Shiny.BluetoothLE;
using VegaBridgeApp.Models.BLE;

namespace VegaBridgeApp.Services.BLE;

/// <summary>
/// Central BLE service that manages scanning and connecting using Shiny.BluetoothLE.
/// Implements a reactive state machine to provide a predictable API for the UI.
/// </summary>
public class BleManagerService(IBleManager bleManager) : IDisposable
{
    private IPeripheral? _activePeripheral;
    private IDisposable? _scanSubscription;
    private IDisposable? _connectionSubscription;
    private CancellationTokenSource? _scanTimeoutCts;
    private CancellationTokenSource? _retryCts;

    // Maintain our own dictionary of discovered peripherals for reliable access
    private readonly Dictionary<string, IPeripheral> _discoveredPeripherals = new();

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
        // Always stop any existing scan first to prevent "existing scan" errors
        StopScanning();

        if (!await RequestAccessAsync()) return;

        try
        {
            _state.OnNext(BleConnectionState.Scanning);
            
            // Clear discovered devices but preserve the active connection if it exists
            _discoveredPeripherals.Clear();
            if (_activePeripheral != null)
            {
                _discoveredPeripherals[_activePeripheral.Uuid] = _activePeripheral;
            }

            Log.Information("BLE scanning started");
            
            // Subscribe to peripheral updates and keep the subscription to dispose it later
            _scanSubscription = bleManager.ScanForUniquePeripherals().Subscribe(UpdateDeviceFromScanResult);
            
            _scanTimeoutCts = new CancellationTokenSource();
            
            await Task.Delay(TimeSpan.FromSeconds(30), _scanTimeoutCts.Token);
            if (CurrentState == BleConnectionState.Scanning)
            {
                Log.Information("BLE scan automatic timeout reached");
                StopScanning();
            }
        }
        catch (OperationCanceledException)
        {
            /* Expected when scan is stopped manually */
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start BLE scan");
            UpdateError($"Could not start scan: {ex.Message}");
        }
    }

    public void StopScanning()
    {
        Log.Information("Stopping BLE scan");

        // Cancel the timeout timer
        _scanTimeoutCts?.Cancel();
        _scanTimeoutCts?.Dispose();
        _scanTimeoutCts = null;

        // Dispose the subscription to stop receiving scan results
        _scanSubscription?.Dispose();
        _scanSubscription = null;

        // Tell the hardware to stop scanning
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

            // Find peripheral from our maintained dictionary
            string uuidKey = deviceUuid.ToString();
            if (!_discoveredPeripherals.TryGetValue(uuidKey.ToUpper(), out IPeripheral? peripheral))
            {
                UpdateError("Device not found. Please scan again.");
                return false;
            }

            await peripheral.ConnectAsync(timeout: TimeSpan.FromSeconds(30));

            _activePeripheral = peripheral;
            _state.OnNext(BleConnectionState.Connected);

            Log.Information("Successfully connected to {Uuid}", deviceUuid);
            
            // Setup connection monitoring
            SetupConnectionMonitoring(peripheral);

            UpdateDeviceList(); // Refresh connection status in list
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Connection failed for {Uuid}", deviceUuid);
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
            
            // Cancel any ongoing retries and remove monitoring to prevent trigger-loop
            if (_retryCts != null)
            {
                await _retryCts.CancelAsync();
                _retryCts?.Dispose();
                _retryCts = null;
            }
            _connectionSubscription?.Dispose();
            _connectionSubscription = null;

            await _activePeripheral.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error during disconnection");
        }
        finally
        {
            _activePeripheral = null;
            _state.OnNext(BleConnectionState.Idle);
            UpdateDeviceList();
        }
    }

    // ── Connection Monitoring & Retry ─────────────────────────────────────

    private void SetupConnectionMonitoring(IPeripheral peripheral)
    {
        _connectionSubscription?.Dispose();

        Log.Information("Setting up connection monitoring for {Uuid}", peripheral.Uuid);

        // Combine disconnected and connection-failed events
        _connectionSubscription = peripheral.WhenDisconnected()
            .Subscribe(_ => HandleUnexpectedDisconnection(peripheral));
    }

    private async void HandleUnexpectedDisconnection(IPeripheral peripheral)
    {
        // Check if this was an intentional disconnect (managed by _activePeripheral == null)
        if (_activePeripheral == null) return;

        Log.Warning("Unexpected disconnection detected for {Uuid}", peripheral.Uuid);
        
        // Update state immediately to inform UI
        _state.OnNext(BleConnectionState.Idle);
        UpdateDeviceList();
        UpdateError("Connection lost. Attempting to reconnect...");

        await RetryConnectionAsync(Guid.Parse(peripheral.Uuid));
    }

    private async Task RetryConnectionAsync(Guid deviceUuid)
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
                Log.Information("Retry connection attempt {Attempt}/{Max} for {Uuid}", attempt, maxRetries, deviceUuid);
                
                // Wait before retrying (exponential backoff: 2, 4, 8 seconds)
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), token);

                if (!await ConnectAsync(deviceUuid)) continue;
                Log.Information("Successfully reconnected to {Uuid} on attempt {Attempt}", deviceUuid, attempt);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Retry attempt {Attempt} failed for {Uuid}", attempt, deviceUuid);
            }
        }

        if (!token.IsCancellationRequested)
        {
            Log.Error("All reconnection attempts failed for {Uuid}", deviceUuid);
            UpdateError("Connection lost. Reconnection attempts failed.");
        }
    }

    private void UpdateDeviceFromScanResult(IPeripheral result)
    {
        // Store the peripheral for later connection
        _discoveredPeripherals[result.Uuid] = result;

        // Refresh the device list
        UpdateDeviceList();
    }

    private void UpdateDeviceList()
    {
        List<BleDeviceInfo> list =
        [
            .. _discoveredPeripherals.Values
                .Where(p => !string.IsNullOrWhiteSpace(p.Name) && p.Name != "Unknown")
                .Select(p => new BleDeviceInfo
                {
                    Uuid = Guid.Parse(p.Uuid),
                    Name = p.Name!,
                    IsConnected = p.IsConnected(),
                    LastSeen = DateTime.Now
                })
        ];

        _devices.OnNext(list);
    }

    private void UpdateError(string message)
    {
        _errorMessage.OnNext(message);
        _state.OnNext(BleConnectionState.Error);
    }

    public void Dispose()
    {
        StopScanning();
        _connectionSubscription?.Dispose();
        _retryCts?.Cancel();
        _retryCts?.Dispose();
        _state.Dispose();
        _devices.Dispose();
        _errorMessage.Dispose();
    }
}