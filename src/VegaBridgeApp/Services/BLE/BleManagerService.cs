using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Shiny.BluetoothLE;
using VegaBridgeApp.Models.Ble;

namespace VegaBridgeApp.Services.BLE;

/// <summary>
/// Central BLE service that manages scanning, connecting, and data transmission.
/// Wraps Shiny.BluetoothLE and dispatches manufacturer-specific plugins.
/// </summary>
public class BleManagerService(IBleManager bleManager) : IDisposable
{
    private readonly List<IBleDevicePlugin> _plugins =
    [
        new MvAgustaBlePlugin()
    ];

    private IPeripheral? _connectedPeripheral;
    private BleCharacteristicInfo? _writeCharInfo;
    private BleCharacteristicInfo? _readCharInfo;
    private IBleDevicePlugin? _activePlugin;

    private IDisposable? _scanSub;
    private IDisposable? _notifySub;

    private readonly ConcurrentDictionary<string, (IPeripheral Peripheral, int Rssi)> _discoveredPeripherals = new();

    // ── Events for the UI layer ───────────────────────────────────────────

    public event Action? OnScanStarted;
    public event Action? OnScanStopped;
    public event Action<BleDeviceInfo>? OnDeviceDiscovered;
    public event Action<BleConnectionState>? OnConnectionStateChanged;
    public event Action<string>? OnError;
    public event Action<string>? OnFrameReceived;

    // ── Observable state for UI ────────────────────────────────────────────

    public IReadOnlyCollection<BleDeviceInfo> DiscoveredDevices => _discoveredPeripherals
        .Select(kvp => new BleDeviceInfo
        {
            Name = kvp.Value.Peripheral.Name ?? "Unknown",
            Uuid = kvp.Value.Peripheral.Uuid,
            Rssi = kvp.Value.Rssi,
            IsConnected = kvp.Value.Peripheral.IsConnected(),
            FirstDiscovered = DateTime.Now,
            IsConnectable = true
        })
        .ToList();

    public bool IsScanning { get; private set; }
    public BleConnectionState ConnectionState { get; private set; } = BleConnectionState.Unknown;
    public IReadOnlyList<IBleDevicePlugin> Plugins => _plugins.AsReadOnly();
    public IBleDevicePlugin? ActivePlugin => _activePlugin;

    // ── Public API ────────────────────────────────────────────────────────

    public async Task<bool> RequestAccessAsync()
    {
        try
        {
            AccessState access = await bleManager
                .RequestAccess()
                .ToTask();

            bool available = access == AccessState.Available;
            ConnectionState = available
                ? BleConnectionState.Disconnected
                : BleConnectionState.NoBle;
            OnConnectionStateChanged?.Invoke(ConnectionState);

            return available;
        }
        catch (Exception ex)
        {
            ConnectionState = BleConnectionState.Error;
            OnError?.Invoke($"BLE access denied: {ex.Message}");
            return false;
        }
    }

    public void StartScanning()
    {
        if (IsScanning) return;

        IsScanning = true;
        ConnectionState = BleConnectionState.Scanning;
        OnScanStarted?.Invoke();
        OnConnectionStateChanged?.Invoke(ConnectionState);

        string[] serviceUuids = _plugins
            .Select(p => p.ServiceUuid)
            .Distinct()
            .ToArray();

        ScanConfig config = serviceUuids.Length > 0
            ? new ScanConfig { ServiceUuids = serviceUuids }
            : new ScanConfig();

        _scanSub = bleManager
            .Scan(config)
            .Subscribe(
                onNext: scanResult =>
                {
                    IPeripheral? peripheral = scanResult.Peripheral;
                    if (peripheral == null) return;

                    _discoveredPeripherals[peripheral.Uuid] = (peripheral, scanResult.Rssi);

                    OnDeviceDiscovered?.Invoke(new BleDeviceInfo
                    {
                        Name = peripheral.Name ?? "Unknown",
                        Uuid = peripheral.Uuid,
                        Rssi = scanResult.Rssi,
                        IsConnected = peripheral.IsConnected(),
                        FirstDiscovered = DateTime.Now,
                        IsConnectable = true
                    });
                },
                onError: ex =>
                {
                    IsScanning = false;
                    OnScanStopped?.Invoke();
                    OnError?.Invoke($"Scan error: {ex.Message}");
                });
    }

    public void StopScanning()
    {
        _scanSub?.Dispose();
        _scanSub = null;
        IsScanning = false;
        ConnectionState = BleConnectionState.Disconnected;
        OnScanStopped?.Invoke();
        OnConnectionStateChanged?.Invoke(ConnectionState);
    }

    /// <summary>
    /// Loads peripherals that are already paired at the OS level (iOS Settings > Bluetooth).
    /// On iOS, system-bonded devices may not appear in scans, so we must retrieve them directly.
    /// This is the primary way to find a motorcycle on iOS — call this first, scan is optional.
    /// </summary>
    public async Task LoadPairedPeripheralsAsync()
    {
        try
        {
            if (!bleManager.CanViewPairedPeripherals())
                return;

            IReadOnlyList<IPeripheral> paired = bleManager.TryGetPairedPeripherals();
            foreach (IPeripheral peripheral in paired)
            {
                _discoveredPeripherals[peripheral.Uuid] = (peripheral, 0);

                OnDeviceDiscovered?.Invoke(new BleDeviceInfo
                {
                    Name = peripheral.Name ?? "Unknown",
                    Uuid = peripheral.Uuid,
                    Rssi = 0,
                    IsConnected = peripheral.IsConnected(),
                    IsPaired = true,
                    FirstDiscovered = DateTime.Now,
                    IsConnectable = true
                });
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: scanning will still find new devices
            System.Diagnostics.Debug.WriteLine($"LoadPairedPeripheralsAsync failed: {ex.Message}");
        }
    }

    public async Task<bool> ConnectAsync(string peripheralUuid, IBleDevicePlugin? plugin = null)
    {
        try
        {
            if (!_discoveredPeripherals.TryGetValue(peripheralUuid, out (IPeripheral Peripheral, int Rssi) pair))
            {
                OnError?.Invoke($"Peripheral {peripheralUuid} not found");
                return false;
            }

            IPeripheral peripheral = pair.Peripheral;

            _activePlugin = plugin ?? DetectPlugin(peripheral);
            if (_activePlugin == null)
            {
                OnError?.Invoke($"No plugin found for device {peripheral.Name}");
                return false;
            }

            StopScanning();
            ConnectionState = BleConnectionState.Connecting;
            OnConnectionStateChanged?.Invoke(ConnectionState);

            await peripheral.ConnectAsync(
                new ConnectionConfig { AutoConnect = true },
                CancellationToken.None,
                TimeSpan.FromSeconds(10)
            );

            await DiscoverCharacteristicsAsync(peripheral);

            _connectedPeripheral = peripheral;
            ConnectionState = BleConnectionState.Connected;
            OnConnectionStateChanged?.Invoke(ConnectionState);

            return true;
        }
        catch (BleOperationException ex)
        {
            ConnectionState = BleConnectionState.Error;
            OnError?.Invoke($"GATT Error ({ex.GattStatusCode}): {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            ConnectionState = BleConnectionState.Error;
            OnError?.Invoke($"Connection failed: {ex.Message}");
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_connectedPeripheral == null) return;

        try
        {
            _notifySub?.Dispose();
            _notifySub = null;

            ConnectionState = BleConnectionState.Disconnecting;
            OnConnectionStateChanged?.Invoke(ConnectionState);

            await _connectedPeripheral.DisconnectAsync(CancellationToken.None, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Disconnect failed: {ex.Message}");
        }
        finally
        {
            _connectedPeripheral = null;
            _writeCharInfo = null;
            _readCharInfo = null;
            _activePlugin = null;
            ConnectionState = BleConnectionState.Disconnected;
            OnConnectionStateChanged?.Invoke(ConnectionState);
        }
    }

    public async Task SendFrameAsync(byte[] frame)
    {
        if (_connectedPeripheral == null || _writeCharInfo == null)
        {
            OnError?.Invoke("Not connected – cannot send");
            return;
        }

        try
        {
            if (_activePlugin == null)
            {
                OnError?.Invoke("No active plugin – cannot send");
                return;
            }

            if (!_writeCharInfo.CanWrite())
            {
                OnError?.Invoke("Characteristic is not writable");
                return;
            }

            bool withResponse = _activePlugin.RequiresWriteWithResponse;
            await _connectedPeripheral.WriteCharacteristic(_writeCharInfo, frame, withResponse);
        }
        catch (BleOperationException ex)
        {
            OnError?.Invoke($"Write failed ({ex.GattStatusCode}): {ex.Message}");
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Send failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _scanSub?.Dispose();
        _notifySub?.Dispose();
        if (_connectedPeripheral != null)
        {
            _ = _connectedPeripheral.DisconnectAsync(CancellationToken.None);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private IBleDevicePlugin? DetectPlugin(IPeripheral peripheral)
    {
        string name = peripheral.Name ?? "";
        IEnumerable<IBleDevicePlugin> candidates = _plugins.Where(p =>
            name.Contains(p.DisplayName, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(p.ManufacturerId, StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MV", StringComparison.OrdinalIgnoreCase));

        return candidates.FirstOrDefault() ?? _plugins.FirstOrDefault();
    }

    private async Task DiscoverCharacteristicsAsync(IPeripheral peripheral)
    {
        if (_activePlugin == null) return;

        IReadOnlyList<BleCharacteristicInfo> characteristics = await peripheral
            .GetAllCharacteristics()
            .ToTask();

        foreach (BleCharacteristicInfo info in characteristics)
        {
            if (info.Uuid == _activePlugin.WriteCharacteristicUuid)
            {
                _writeCharInfo = info;
            }
            else if (info.Uuid == _activePlugin.ReadCharacteristicUuid)
            {
                _readCharInfo = info;
                if (info.CanNotify())
                {
                    SubscribeToNotifications(peripheral, info);
                }
                else
                {
                    OnError?.Invoke($"Read characteristic ({info.Uuid}) does not support notifications");
                }
            }
        }

        if (_writeCharInfo == null)
            OnError?.Invoke($"Write characteristic ({_activePlugin.WriteCharacteristicUuid}) not found");
    }

    private void SubscribeToNotifications(IPeripheral peripheral, BleCharacteristicInfo info)
    {
        _notifySub = peripheral
            .NotifyCharacteristic(info)
            .Subscribe(
                onNext: data =>
                {
                    byte[]? raw = data.Data;
                    if (raw == null || raw.Length == 0) return;

                    IBleDevicePlugin? plugin = _activePlugin;
                    if (plugin == null) return;

                    if (plugin.TryParseFrame(raw, out string command, out string[] fields))
                    {
                        string parsed = $"{command}: {string.Join(", ", fields)}";
                        OnFrameReceived?.Invoke(parsed);
                    }
                    else
                    {
                        OnFrameReceived?.Invoke($"Raw: {BitConverter.ToString(raw)}");
                    }
                },
                onError: ex =>
                {
                    OnError?.Invoke($"Notify error: {ex.Message}");
                });
    }
}