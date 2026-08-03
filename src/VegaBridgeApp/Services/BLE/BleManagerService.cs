using System.Collections.Concurrent;
using Bluetooth.Abstractions.Options;
using Bluetooth.Abstractions.Scanning;
using Bluetooth.Abstractions.Scanning.EventArgs;
using Bluetooth.Abstractions.Scanning.Options;
using Serilog;
using VegaBridgeApp.Models.BLE;
using VegaBridgeApp.Services.BLE.Plugins;

namespace VegaBridgeApp.Services.BLE;

/// <summary>
/// Central BLE service that manages scanning, connecting, and data transmission.
/// Wraps Bluetooth.Maui and dispatches manufacturer-specific plugins.
/// </summary>
public class BleManagerService(IBluetoothScanner bleScanner) : IDisposable
{
    private readonly List<IBleDevicePlugin> _plugins =
    [
        new MvAgustaBlePlugin()
    ];

    private IBluetoothRemoteDevice? _connectedDevice;
    private IBleDevicePlugin? _activePlugin;

    private readonly ConcurrentDictionary<string, (IBluetoothRemoteDevice Device, int Rssi)> _discoveredDevices = new();

    // ── Events for the UI layer ───────────────────────────────────
    public event Action<BleDeviceInfo>? OnDeviceDiscovered;
    public event Action<BleDeviceInfo>? OnDeviceDisappeared;
    public event Action<BleConnectionState>? OnConnectionStateChanged;
    public event Action<string>? OnError;

    /// <summary>Fired when a complete frame is received from the connected bike.</summary>
    public event Action<string>? OnFrameReceived;

    // ── Observable state for UI ────────────────────────────────────

    private bool _isScanning;
    public bool IsConnected => _connectedDevice is not null && _connectedDevice.IsConnected;
    public IReadOnlyList<IBleDevicePlugin> Plugins => _plugins.AsReadOnly();
    public IBleDevicePlugin? ActivePlugin => _activePlugin;

    // ── Public API ────────────────────────────────────────────────

    public async Task<bool> RequestAccessAsync()
    {
        try
        {
            bool hasAccess = await bleScanner.HasScannerPermissionsAsync();

            if (!hasAccess)
            {
                await bleScanner.RequestScannerPermissionsAsync(true);
            }
            else
            {
                return true;
            }

            hasAccess = await bleScanner.HasScannerPermissionsAsync();

            return !hasAccess ? throw new Exception("BLE access denied") : true;
        }
        catch (Exception)
        {
            OnConnectionStateChanged?.Invoke(BleConnectionState.Error);
            return false;
        }
    }

    public async Task StartScanningAsync()
    {
        if (_isScanning) return;

        _isScanning = true;
        OnConnectionStateChanged?.Invoke(BleConnectionState.Scanning);

        // Build scan config with Service UUID filter
        Guid[] serviceUuids = _plugins.Select(p => p.ServiceUuid).Distinct().ToArray();
        ScanningOptions scanningOptions = new()
        {
            ScanMode = BluetoothScanMode.LowLatency,
            IgnoreNamelessAdvertisements = true,
            IgnoreDuplicateAdvertisements = true
            //ServiceUuids = serviceUuids.Length > 0 ? serviceUuids : null
        };

        // Attach event handlers
        bleScanner.AdvertisementReceived += OnAdvertisementReceived;
        bleScanner.DeviceListChanged += OnDeviceListChanged;

        try
        {
            await bleScanner.StartScanningIfNeededAsync(scanningOptions);
            Log.Information("BLE scanning started (filtered: {Filtered})", serviceUuids.Length > 0);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BLE scanning failed");
            CleanupScanningEvents();
            _isScanning = false;
            OnError?.Invoke($"Scan failed: {ex.Message}");
        }
    }

    private void OnDeviceListChanged(object? sender, DeviceListChangedEventArgs e)
    {
        if (e.AddedItems != null)
        {
            foreach (IBluetoothRemoteDevice device in e.AddedItems)
            {
                BleDeviceInfo info = new()
                {
                    Name = device.Name ?? "Unknown",
                    Uuid = device.Id,
                    Rssi = device.SignalStrengthDbm,
                    IsConnected = device.IsConnected,
                    FirstDiscovered = DateTime.Now,
                    IsConnectable = true
                };
                _discoveredDevices.TryAdd(device.Id, (device, device.SignalStrengthDbm));
                OnDeviceDiscovered?.Invoke(info);
            }
        }

        if (e.RemovedItems == null) return;
        {
            foreach (IBluetoothRemoteDevice device in e.RemovedItems)
            {
                if (!_discoveredDevices.TryRemove(device.Id, out (IBluetoothRemoteDevice Device, int Rssi) tuple)) continue;
                BleDeviceInfo info = new()
                {
                    Name = tuple.Device.Name ?? "Unknown",
                    Uuid = device.Id,
                    Rssi = tuple.Rssi,
                    IsConnected = false,
                    FirstDiscovered = DateTime.Now,
                    IsConnectable = true
                };
                OnDeviceDisappeared?.Invoke(info);
            }
        }
    }

    private void OnAdvertisementReceived(object? sender, AdvertisementReceivedEventArgs e)
    {
        Log.Debug("Advertisement from: {Name}", e.Advertisement.DeviceName ?? "Unnamed");
    }

    private void CleanupScanningEvents()
    {
        bleScanner.AdvertisementReceived -= OnAdvertisementReceived;
        bleScanner.DeviceListChanged -= OnDeviceListChanged;
    }

    public  IReadOnlyList<IBluetoothRemoteDevice> GetDevices()
    {
        return bleScanner.GetDevices();
    }

    // ── Connection ─────────────────────────────────────────────

    public async Task<bool> ConnectAsync(string deviceUuid, IBleDevicePlugin? plugin)
    {
        try
        {
            if (_connectedDevice is not null && _connectedDevice.IsConnected)
            {
                Log.Warning("Already connected to a device; disconnect first.");
                return false;
            }

            OnConnectionStateChanged?.Invoke(BleConnectionState.Connecting);

            IBluetoothRemoteDevice? device = bleScanner.GetDevice(d => d.Id == deviceUuid);
            if (device is null)
            {
                OnError?.Invoke($"Device {deviceUuid} not found in cache. Start scanning first.");
                OnConnectionStateChanged?.Invoke(BleConnectionState.Disconnected);
                return false;
            }

            ConnectionOptions options = new()
            {
                ConnectionRetry = RetryOptions.Default
            };

            await device.ConnectAsync(options, TimeSpan.FromSeconds(30));
            _connectedDevice = device;

            // Wire up connection-lifecycle events
            device.Connected += (_, _) =>
            {
                Log.Information("BLE device connected: {Name}", device.Name);
                OnConnectionStateChanged?.Invoke(BleConnectionState.Connected);
            };

            device.Disconnected += (_, _) =>
            {
                Log.Information("BLE device disconnected: {Name}", device.Name);
                OnConnectionStateChanged?.Invoke(BleConnectionState.Disconnected);
                _connectedDevice = null;
                _activePlugin = null;
            };

            device.UnexpectedDisconnection += (_, _) =>
            {
                Log.Warning("BLE unexpected disconnection: {Name}", device.Name);
                OnConnectionStateChanged?.Invoke(BleConnectionState.Error);
            };

            // Resolve plugin (auto-detect if not supplied)
            _activePlugin = plugin ?? DetectPlugin(device);
            if (_activePlugin is null)
            {
                OnError?.Invoke("No matching BLE plugin found for this device.");
                await DisconnectAsync();
                return false;
            }

            // Discover services and characteristics
            await device.ExploreServicesAsync(new ServiceExplorationOptions
            {
                ExploreCharacteristics = true,
                ExploreDescriptors = true,
                UseCache = true
            });

            Log.Information("Connected to {Name} via {Plugin}", device.Name, _activePlugin.DisplayName);
            OnConnectionStateChanged?.Invoke(BleConnectionState.Connected);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BLE connect failed for {Uuid}", deviceUuid);
            OnError?.Invoke(ex.Message);
            OnConnectionStateChanged?.Invoke(BleConnectionState.Error);
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_connectedDevice is null)
            return;

        try
        {
            OnConnectionStateChanged?.Invoke(BleConnectionState.Disconnecting);
            await _connectedDevice.DisconnectAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BLE disconnect error");
        }
        finally
        {
            _connectedDevice = null;
            _activePlugin = null;
            OnConnectionStateChanged?.Invoke(BleConnectionState.Disconnected);
        }
    }

    // ── Data Transmission ──────────────────────────────────────

    public async Task<bool> SendControlDataAsync(byte[] data)
    {
        return await SendInternalAsync(data, isControl: true);
    }

    public async Task<bool> SendDataAsync(byte[] data)
    {
        return await SendInternalAsync(data, isControl: false);
    }

    private async Task<bool> SendInternalAsync(byte[] data, bool isControl)
    {
        if (_connectedDevice is null || !_connectedDevice.IsConnected || _activePlugin is null)
        {
            OnError?.Invoke("Not connected to a device or no active plugin.");
            return false;
        }

        try
        {
            return await _activePlugin.SendAsync(_connectedDevice, data, isControl);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send data via plugin {Plugin}", _activePlugin.DisplayName);
            OnError?.Invoke(ex.Message);
            return false;
        }
    }

    // ── Notifications ──────────────────────────────────────────

    public async Task StartNotificationsAsync()
    {
        if (_connectedDevice is null || !_connectedDevice.IsConnected)
        {
            OnError?.Invoke("Not connected to a device.");
            return;
        }

        try
        {
            IBluetoothRemoteCharacteristic? characteristic = ResolveReadCharacteristic();
            if (characteristic is null)
            {
                OnError?.Invoke("Read characteristic not found.");
                return;
            }

            characteristic.ValueUpdated += OnReadCharacteristicValueUpdated;
            await characteristic.StartListeningAsync(TimeSpan.FromSeconds(10), default);

            Log.Information("Subscribed to read characteristic notifications");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start notifications");
            OnError?.Invoke(ex.Message);
        }
    }

    public async Task StopNotificationsAsync()
    {
        if (_connectedDevice is null)
            return;

        try
        {
            IBluetoothRemoteCharacteristic? characteristic = ResolveReadCharacteristic();
            if (characteristic is null)
                return;

            characteristic.ValueUpdated -= OnReadCharacteristicValueUpdated;
            await characteristic.StopListeningAsync(TimeSpan.FromSeconds(10), default);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to stop notifications");
        }
    }

    // ── Private helpers ────────────────────────────────────────────

    private IBleDevicePlugin? DetectPlugin(IBluetoothRemoteDevice device)
    {
        foreach (IBleDevicePlugin plugin in _plugins)
        {
            if (device.HasService(plugin.ServiceUuid))
                return plugin;
        }

        return null;
    }

    private IBluetoothRemoteCharacteristic? ResolveReadCharacteristic()
    {
        if (_connectedDevice is null || _activePlugin is null)
            return null;

        IBluetoothRemoteService? service = _connectedDevice.GetService(_activePlugin.ServiceUuid);

        IBluetoothRemoteCharacteristic? characteristic = service.GetCharacteristicOrDefault(Guid.Parse(_activePlugin.ReadCharacteristicUuid));
        return characteristic ??
               // Fallback: any characteristic with notify/indicate
               service.GetCharacteristicOrDefault(c => c.CanListen);
    }

    private void OnReadCharacteristicValueUpdated(object? sender, ValueUpdatedEventArgs e)
    {
        try
        {
            byte[] bytes = e.NewValue.ToArray();
            string hex = Convert.ToHexString(bytes);
            Log.Debug("RX {Bytes} bytes: {Hex}", bytes.Length, hex);

            if (_activePlugin is not null && _activePlugin.IsValidFrame(bytes))
            {
                if (!_activePlugin.TryParseFrame(bytes, out string command, out string[] fields)) return;
                string frame = "\r" + command + "\x1E" + string.Join("\x1E", fields) + "\r";
                OnFrameReceived?.Invoke(frame);
            }
            else
            {
                // Unknown frame — forward raw as hex
                OnFrameReceived?.Invoke(hex);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error parsing incoming BLE frame");
        }
    }

    public void Dispose()
    {
        if (_connectedDevice is not null)
        {
            _ = _connectedDevice.DisconnectAsync();
            _connectedDevice = null;
        }

        _activePlugin = null;
    }
}