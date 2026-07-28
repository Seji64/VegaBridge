using Microsoft.AspNetCore.Components;
using Microsoft.Maui.Storage;
using MudBlazor;
using VegaBridgeApp.Models.Ble;
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
    protected IBleDevicePlugin? ManualPlugin { get; set; }
    protected bool IsConnected => ConnectionState == BleConnectionState.Connected;

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

        // Subscribe to BLE manager events
        BleManager.OnDeviceDiscovered += OnDeviceDiscovered;
        BleManager.OnConnectionStateChanged += OnConnectionChanged;
        BleManager.OnError += OnError;
        BleManager.OnFrameReceived += OnFrameReceived;

        // Request BLE access (triggers permission dialog)
        bool hasAccess = await BleManager.RequestAccessAsync();

        if (hasAccess)
        {
            // Load already-paired devices (primary flow on iOS)
            BleManager.LoadPairedPeripherals();

            // iOS specific: Start scanning to find bonded devices 
            // as they often don't appear in LoadPairedPeripherals
            BleManager.StartScanning();

            // Auto-stop scanning after 15 seconds to save battery
            _ = Task.Run(async () =>
            {
                await Task.Delay(15000);
                if (ConnectionState != BleConnectionState.Connected)
                {
                    await InvokeAsync(() => BleManager.StopScanning());
                }
            });
        }
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

    private void OnDeviceDiscovered(BleDeviceInfo device)
    {
        _ = InvokeAsync(() =>
        {
            BleDeviceInfo? existing = Devices.FirstOrDefault(d => d.Uuid == device.Uuid);
            if (existing != null)
            {
                existing.Rssi = device.Rssi;
                existing.IsConnected = device.IsConnected;
            }
            else
            {
                Devices.Add(device);
            }

            StateHasChanged();
        });
    }

    private void OnConnectionChanged(BleConnectionState state)
    {
        _ = InvokeAsync(() =>
        {
            ConnectionState = state;
            StatusMessage = state switch
            {
                BleConnectionState.Connected => L["BLEConnected"],
                BleConnectionState.Connecting => L["BLEConnecting"],
                BleConnectionState.Disconnecting => L["BLEDisconnecting"],
                BleConnectionState.Disconnected => L["BLEDisconnected"],
                BleConnectionState.Error => L["BLEError"],
                _ => StatusMessage
            };

            // Sync IsConnected state in the device list
            BleDeviceInfo? device = Devices.FirstOrDefault(d => d.Uuid == _selectedUuid);
            if (device != null)
                device.IsConnected = state == BleConnectionState.Connected;

            StateHasChanged();
        });
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
            LastReceivedFrame = frame;
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
        }
        else if (string.IsNullOrEmpty(StatusMessage) || !StatusMessage.Contains(L["BLEError"]))
        {
            StatusMessage = L["BLEConnectFailed"];
        }
    }

    private async Task Disconnect()
    {
        await BleManager.DisconnectAsync();
    }

    private async Task SendTestFrame()
    {
        IBleDevicePlugin? plugin = BleManager.ActivePlugin;
        if (plugin == null)
        {
            Snackbar.Add(L["BLENoPlugin"], Severity.Warning);
            return;
        }

        byte[] frame = plugin.CreateTestFrame();
        await BleManager.SendFrameAsync(frame);
        Snackbar.Add(L["BLETestSent"], Severity.Success);
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
        BleManager.OnConnectionStateChanged -= OnConnectionChanged;
        BleManager.OnError -= OnError;
        BleManager.OnFrameReceived -= OnFrameReceived;

        await BleManager.DisconnectAsync();
    }
}
