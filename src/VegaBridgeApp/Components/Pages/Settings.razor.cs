using Microsoft.AspNetCore.Components;

using MudBlazor;
using Serilog;
using System.Text;
using CommunityToolkit.Maui.Storage;
using VegaBridgeApp.Models.BLE;
using VegaBridgeApp.Models.Geocoding;
using VegaBridgeApp.Services.BLE.Plugins;
using VegaBridgeApp.Models.BLE.MvAgusta;

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

    /// <summary>
    /// A/B test switch for the GUI1-echo response (Settings page).
    /// Default OFF – official capture shows the phone never writes GUI1.
    /// Backed by the static MvAgustaBlePlugin.Gui1ResponseEnabled flag,
    /// so it takes effect immediately, even mid-connection.
    /// </summary>
    private bool Gui1ResponseEnabled
    {
        get => MvAgustaBlePlugin.Gui1ResponseEnabled;
        set => MvAgustaBlePlugin.Gui1ResponseEnabled = value;
    }

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

    /// <summary>
    /// Quick test sender for a complete navigation sequence.
    /// Uses the capture-proven frame formats (DEST/REM/NAVI/SM/SM1/FINISH).
    /// No GUI1 send needed – the GUI1 response happens automatically in the plugin
    /// as a reply to every bike notification (write with response).
    /// Rapid-fire: only short write pauses (150 ms), no 5–10 s wait times.
    /// </summary>
    private async Task SendNavigationTestSequenceAsync()
    {
        if (!IsConnected) return;

        BleCommandLogger.ClearLog();
        BleCommandLogger.Log("=== TEST SEQUENCE START (Standard, rapid-fire) ===");
        StatusMessage = "Sending standard nav test sequence...";
        StateHasChanged();

        // Phase 1: Navigationsstart (DEST/REM – capture-proven Format)
        // DEST|""|lon|lat – Feld 1 (Adresse) LEER!
        await BleManager.SendCommandAsync(Commands.DEST, "", "9.258020", "48.775730");
        await Task.Delay(150);
        // REM|""|<meter>|"" – trailing empty field
        await BleManager.SendCommandAsync(Commands.REM, "", "10500", "");
        await Task.Delay(150);

        // Phase 1: Links abbiegen – NAVI|icon|guide|intersectionName
        await BleManager.SendCommandAsync(Commands.NAVI, "turn-left", "Links abbiegen\nHauptstraße", "Hauptstraße");
        await Task.Delay(150);
        // SM|0|<remaining>|<distToTurn>
        await BleManager.SendCommandAsync(Commands.SM, "0", "10500", "250");
        await Task.Delay(150);
        // SM1|902|<countdown>| – Links-Countdown
        await BleManager.SendCommandAsync(Commands.SM1, "902", "7", "");
        await Task.Delay(150);

        StatusMessage = "Test: Links abbiegen gesendet...";
        StateHasChanged();

        // Phase 2: Rechts abbiegen
        await BleManager.SendCommandAsync(Commands.NAVI, "turn-right", "Rechts abbiegen\nNebenstraße", "Nebenstraße");
        await Task.Delay(150);
        await BleManager.SendCommandAsync(Commands.SM, "0", "10200", "180");
        await Task.Delay(150);
        // SM1|901|<countdown>| – Rechts-Countdown
        await BleManager.SendCommandAsync(Commands.SM1, "901", "6", "");
        await Task.Delay(150);

        StatusMessage = "Test: Rechts abbiegen gesendet...";
        StateHasChanged();

        // Ende: Navigation beenden
        await BleManager.SendCommandAsync(Commands.FINISH, "", "", "");
        BleCommandLogger.Log("=== TEST SEQUENCE END (Standard) ===");
        StatusMessage = "Test sequence complete.";
        StateHasChanged();
    }

    private async Task ExportBleLogAsync()
    {
        IReadOnlyList<string> lines = BleCommandLogger.GetLog();
        if (lines.Count == 0)
        {
            Snackbar.Add("BLE log is empty.", Severity.Info);
            return;
        }

        string content = string.Join('\n', lines);
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        using MemoryStream stream = new(bytes);

        string filename = $"ble_log_{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}.txt";
        FileSaverResult saveResult = await FileSaver.Default.SaveAsync(filename, stream, CancellationToken.None);

        if (saveResult.IsSuccessful)
        {
            Snackbar.Add($"BLE log saved ({lines.Count} lines).", Severity.Success);
        }
        else
        {
            Snackbar.Add(saveResult.Exception?.Message ?? "Save cancelled.", Severity.Error);
        }

        BleCommandLogger.ClearLog();
    }

    private void ClearBleLog()
    {
        BleCommandLogger.ClearLog();
        Snackbar.Add("BLE log cleared.", Severity.Info);
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
