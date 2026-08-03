using Bluetooth.Abstractions.Scanning;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Serilog;
using VegaBridgeApp.Models.BLE;
using VegaBridgeApp.Models.Geocoding;
using VegaBridgeApp.Services.BLE;

namespace VegaBridgeApp.Components.Pages;

public partial class Settings : ComponentBase, IAsyncDisposable
{
    // ── UI-bound state ────────────────────────────────────────────────────

    private List<BleDeviceInfo> Devices { get; set; } = [];
    private BleConnectionState ConnectionState { get; set; } = BleConnectionState.Unknown;

    private BleDeviceInfo? SelectedDevice
    {
        get => Devices.FirstOrDefault(d => d.Uuid == _selectedUuid);
        set => _selectedUuid = value?.Uuid;
    }
    private string? _selectedUuid;

    private string? StatusMessage { get; set; }
    private List<string> ReceivedFrames { get; } = [];
    private IBleDevicePlugin? ActivePlugin => BleManager.ActivePlugin;
    private IBleDevicePlugin? ManualPlugin { get; set; }
    private bool IsConnected => ConnectionState == BleConnectionState.Connected;
    public bool IsScanning => ConnectionState == BleConnectionState.Scanning;

    private double _offRouteThreshold = 10;
    private GeoResult? _homeLocation;
    
    protected override async Task OnInitializedAsync()
    {
        _offRouteThreshold = Preferences.Get("off_route_threshold", 10.0);

        // Home aus Preferences laden
        string homeLabel = Preferences.Get("home_label", "");
        double homeLat = Preferences.Get("home_lat", 0.0);
        double homeLon = Preferences.Get("home_lon", 0.0);
        if (!string.IsNullOrEmpty(homeLabel) && homeLat != 0 && homeLon != 0)
        {
            _homeLocation = new GeoResult(homeLabel, homeLat, homeLon, "home");
        }
        
        
        // Request BLE access (triggers permission dialog)
        bool hasAccess = await BleManager.RequestAccessAsync();
        
        if (!hasAccess)
        {
            Snackbar.Add("No BLE Permission Detected", Severity.Error);
            return;
        }
        
        // Subscribe to BLE manager events
        BleManager.OnDeviceDiscovered += OnDeviceDiscovered;
        BleManager.OnDeviceDisappeared += OnDeviceDisappeared;
        BleManager.OnConnectionStateChanged += OnConnectionStateChanged;
        BleManager.OnError += OnError;
        BleManager.OnFrameReceived += OnFrameReceived;
        
        await StartBleScanAsync();
    }

    private async Task StartBleScanAsync()
    {
        await BleManager.StartScanningAsync();
    }

    private void OnConnectionStateChanged(BleConnectionState obj)
    {
        Log.Debug("New connection state: {state}", obj);
        ConnectionState = obj;
        StateHasChanged();
    }

    private MudBlazor.Color GetStatusSeverity()
    {
        return ConnectionState switch
        {
            BleConnectionState.Connected => MudBlazor.Color.Success,
            BleConnectionState.Connecting => MudBlazor.Color.Warning,
            BleConnectionState.Error => MudBlazor.Color.Error,
            _ => MudBlazor.Color.Default
        };
    }

    // ── Event handlers (called from BLE manager thread) ────────────────────

    private void OnDeviceDisappeared(BleDeviceInfo device)
    {
        BleDeviceInfo? existing = Devices.FirstOrDefault(d => d.Uuid == device.Uuid);
        if (existing != null)
        {
            Devices.Remove(existing);
        }
        
        StateHasChanged();
    }

    private void UpdateDevices()
    {
        IReadOnlyList<IBluetoothRemoteDevice> devices =  BleManager.GetDevices();

        Devices = devices.Select(x => new BleDeviceInfo()
        {
            Name = x.Name ?? "Unknown",
            Uuid = x.Id,
            Rssi = x.SignalStrengthDbm
        }).ToList();

        StateHasChanged();
    }
    
    private void OnDeviceDiscovered(BleDeviceInfo device)
    {
       UpdateDevices();
    }
    
    private void OnError(string message)
    {
        _ = InvokeAsync(() =>
        {
            StatusMessage = L["BLEError"] + ": " + message;
            StateHasChanged();
        });
    }

    private void OnFrameReceived(string frame)
    {
        _ = InvokeAsync(() =>
        {
            ReceivedFrames.Add(frame);
            if (ReceivedFrames.Count > 100)
                ReceivedFrames.RemoveAt(0);
            StateHasChanged();
        });
    }

    // ── User actions ──────────────────────────────────────────────────────

    private async Task ConnectToSelected()
    {
        BleDeviceInfo? device = SelectedDevice;
        if (device == null) return;

        StatusMessage = string.Format(L["BLEConnectingTo"], device.Name);
        StateHasChanged();

        IBleDevicePlugin? plugin = ManualPlugin;
        bool success = await BleManager.ConnectAsync(device.Uuid, plugin);

        if (success)
        {
            StatusMessage = string.Format(L["BLEConnectedTo"], device.Name);
            // Start listening for incoming frames from the bike
            await BleManager.StartNotificationsAsync();
        }
        else if (string.IsNullOrEmpty(StatusMessage) || !StatusMessage.Contains(L["BLEError"]))
        {
            StatusMessage = L["BLEConnectFailed"];
        }

        StateHasChanged();
    }

    private async Task Disconnect()
    {
        await BleManager.StopNotificationsAsync();
        await BleManager.DisconnectAsync();
        StatusMessage = L["BLEDisconnected"];
        StateHasChanged();
    }

    private async Task SendTestFrame()
    {
        if (ActivePlugin == null) return;
        byte[] testFrame = ActivePlugin.CreateTestFrame();
        bool success = await BleManager.SendControlDataAsync(testFrame);
        if (success)
        {
            StatusMessage = L["BLETestSent"];
            StateHasChanged();
        }
    }

    private void OnManualPluginChanged(string? manufacturerId)
    {
        ManualPlugin = string.IsNullOrEmpty(manufacturerId)
            ? null
            : BleManager.Plugins.FirstOrDefault(p => p.ManufacturerId == manufacturerId);
    }

    private void SaveOffRouteThreshold()
    {
        Preferences.Set("off_route_threshold", _offRouteThreshold);
    }

    private async Task<IEnumerable<GeoResult>> HomeSearchAsync(string query, CancellationToken ct)
    {
        return await GeocodingService.SuggestAsync(query, ct: ct);
    }

    private void OnHomeSelected(GeoResult? location)
    {
        _homeLocation = location;
        if (location != null)
        {
            Preferences.Set("home_label", location.Label);
            Preferences.Set("home_lat", location.Latitude);
            Preferences.Set("home_lon", location.Longitude);
        }
        else
        {
            Preferences.Remove("home_label");
            Preferences.Remove("home_lat");
            Preferences.Remove("home_lon");
        }
        StateHasChanged();
    }

    // ── Cleanup ───────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        BleManager.OnDeviceDiscovered -= OnDeviceDiscovered;
        BleManager.OnDeviceDisappeared -= OnDeviceDisappeared;
        BleManager.OnConnectionStateChanged -= OnConnectionStateChanged;
        BleManager.OnError -= OnError;
        BleManager.OnFrameReceived -= OnFrameReceived;
        await BleManager.StopNotificationsAsync();
        await BleManager.DisconnectAsync();
    }
}
