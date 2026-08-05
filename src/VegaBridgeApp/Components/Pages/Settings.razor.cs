using Microsoft.AspNetCore.Components;
using MudBlazor;
using Serilog;
using VegaBridgeApp.Models.BLE;
using VegaBridgeApp.Models.Geocoding;

namespace VegaBridgeApp.Components.Pages;

public partial class Settings : ComponentBase, IAsyncDisposable
{
    // ── UI-bound state ────────────────────────────────────────────────────

    private List<BleDeviceInfo> Devices { get; set; } = [];
    private BleConnectionState ConnectionState { get; set; } = BleConnectionState.Idle;

    private BleDeviceInfo? SelectedDevice
    {
        get => Devices.FirstOrDefault(d => d.Uuid == _selectedUuid);
        set => _selectedUuid = value?.Uuid;
    }
    private Guid? _selectedUuid;

    private string? StatusMessage { get; set; }
    private bool IsConnected => BleManager.IsAnyDeviceConnected;
    private bool IsScanning => ConnectionState == BleConnectionState.Scanning;

    private double _offRouteThreshold = 10;
    private GeoResult? _homeLocation;
    
    private IDisposable? _stateSubscription;
    private IDisposable? _devicesSubscription;
    private IDisposable? _errorSubscription;

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
        
        // Subscribe to BLE manager reactive streams
        _stateSubscription = BleManager.State.Subscribe(OnConnectionStateChanged);
        _devicesSubscription = BleManager.Devices.Subscribe(UpdateDevices);
        _errorSubscription = BleManager.ErrorMessages.Subscribe(OnError);

        // Request BLE access (triggers permission dialog)
        bool hasAccess = await BleManager.RequestAccessAsync();
        
        if (!hasAccess)
        {
            Snackbar.Add("No BLE Permission Detected", Severity.Error);
            return;
        }
        
        await StartBleScanAsync();
    }

    private async Task StartBleScanAsync()
    {
        await BleManager.StartScanningAsync();
    }

    private void StopBleScanAsync()
    {
        BleManager.StopScanning();
    }

    private void OnConnectionStateChanged(BleConnectionState obj)
    {
        Log.Debug("New connection state: {state}", obj);
        ConnectionState = obj;
        _ = InvokeAsync(StateHasChanged);
    }

    private string? GetDeviceStatusText(BleDeviceInfo device)
    {
        if (device.IsConnected) return L["Connected"];
        if (SelectedDevice?.Uuid == device.Uuid)
        {
            return ConnectionState switch
            {
                BleConnectionState.Connecting => L["Connecting"],
                BleConnectionState.Error => L["BLEError"],
                _ => null
            };
        }
        return null;
    }

    private MudBlazor.Color GetDeviceStatusColor(BleDeviceInfo device)
    {
        if (device.IsConnected) return MudBlazor.Color.Success;
        if (SelectedDevice?.Uuid == device.Uuid)
        {
            return ConnectionState switch
            {
                BleConnectionState.Connecting => MudBlazor.Color.Warning,
                BleConnectionState.Error => MudBlazor.Color.Error,
                _ => MudBlazor.Color.Default
            };
        }
        return MudBlazor.Color.Default;
    }

    private void UpdateDevices(IReadOnlyList<BleDeviceInfo> devices)
    {
        Devices = [.. devices];
        _ = InvokeAsync(StateHasChanged);
    }
    
    private void OnError(string message)
    {
        _ = InvokeAsync(() =>
        {
            // Use Snackbar for immediate, transient notification
            Snackbar.Add(message, Severity.Error);
            
            // Keep StatusMessage for persistent visibility on the page
            StatusMessage = L["BLEError"] + ": " + message;
            StateHasChanged();
        });
    }

    // ── User actions ──────────────────────────────────────────────────────

    private async Task ConnectToSelected()
    {
        BleDeviceInfo? device = SelectedDevice;
        if (device == null) return;

        string connectingMessage = string.Format(L["BLEConnectingTo"], device.Name);
        StatusMessage = connectingMessage;
        StateHasChanged();

        bool success = await BleManager.ConnectAsync(device.Uuid);

        if (success)
        {
            StatusMessage = string.Format(L["BLEConnectedTo"], device.Name);
        }
        else if (StatusMessage == connectingMessage)
        {
            // Only set generic failure if no specific error message was set by OnError
            StatusMessage = L["BLEConnectFailed"];
        }

        StateHasChanged();
    }

    private async Task Disconnect()
    {
        await BleManager.DisconnectAsync();
        StatusMessage = L["BLEDisconnected"];
        StateHasChanged();
    }

    private async Task SendTestFrame()
    {
        await BleManager.SendTestFrameAsync();
    }

    private async Task HandleScanButtonClick()
    {
        if (IsScanning)
        {
            StopBleScanAsync();
        }
        else
        {
            await StartBleScanAsync();
        }
    }

    private void SaveOffRouteThreshold()
    {
        Preferences.Set("off_route_threshold", _offRouteThreshold);
    }

    private async Task<IEnumerable<GeoResult>>? HomeSearchAsync(string? query, CancellationToken ct)
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
        _stateSubscription?.Dispose();
        _devicesSubscription?.Dispose();
        _errorSubscription?.Dispose();
    }
}
