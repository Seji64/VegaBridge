using Microsoft.AspNetCore.Components;

using MudBlazor;
using Serilog;
using System.Text;
using CommunityToolkit.Maui.Storage;
using VegaBridgeApp.Models.BLE;
using VegaBridgeApp.Models.Geocoding;
using VegaBridgeApp.Services.BLE.Plugins;
using VegaBridgeApp.Models.BLE.MvAgusta;
using VegaBridgeApp.Services.Debug;

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
    
    // ── Long-duration connection test state ──
    private CancellationTokenSource? _longTestCts;
    private bool _longTestRunning;
    private int _longTestStep;
    private const int LongTestTotalSteps = 10;
    private const int LongTestStepDelaySec = 30; // 10 steps × 30s ≈ 5 min
    
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
            } ?? throw new InvalidOperationException();
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

    // ── Debug logging (collects in-memory while enabled) ────────────────

    private bool DebugLoggingEnabled => DebugLogSink.Instance.IsEnabled;

    private void SetDebugLogging(bool enabled)
    {
        DebugLogSink.Instance.SetEnabled(enabled);
        if (enabled)
        {
            DebugLogSink.Instance.Clear(); // start a fresh capture
            Log.Information("Debug logging started (from Settings)");
        }
        else
        {
            Log.Information("Debug logging stopped (from Settings)");
        }
        Snackbar.Add(
            enabled ? L["DebugLoggingOn"] : L["DebugLoggingOff"],
            enabled ? Severity.Success : Severity.Info);
        StateHasChanged();
    }

    private async Task ExportDebugLog()
    {
        string log = DebugLogSink.Instance.GetLog();
        if (string.IsNullOrWhiteSpace(log))
        {
            Snackbar.Add(L["DebugLogEmpty"], Severity.Info);
            return;
        }

        try
        {
            string path = Path.Combine(FileSystem.AppDataDirectory, $"nav-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(path, log);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Nav-Log",
                File = new ShareFile(path)
            });
            Log.Information("Debug log exported: {Path} ({Bytes} bytes)", path, log.Length);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Debug log export failed");
            Snackbar.Add(string.Format(L["DebugExportError"], ex.Message), Severity.Error);
        }
    }

    private void ClearDebugLog()
    {
        DebugLogSink.Instance.Clear();
        Snackbar.Add(L["DebugLogCleared"], Severity.Info);
    }

    // ── Long-duration connection test ────────────────────────────────────
    // Sends a ~5 min navigation simulation (10 steps × 30 s) so the rider
    // can verify the BLE link survives a screen-off ride. Uses the plugin
    // start/finish flow, so the PING keepalive runs during the whole test –
    // exactly like real navigation.

    private bool LongTestRunning => _longTestRunning;

    private string LongTestStatusText
    {
        get
        {
            if (!_longTestRunning) return string.Empty;
            return string.Format(L["LongTestProgress"], _longTestStep, LongTestTotalSteps);
        }
    }

    private async Task StartLongTestAsync()
    {
        if (!IsConnected)
        {
            Snackbar.Add(L["LongTestNotConnected"], Severity.Warning);
            return;
        }

        _longTestCts = new CancellationTokenSource();
        _longTestRunning = true;
        _longTestStep = 0;
        StateHasChanged();

        BleCommandLogger.ClearLog();
        BleCommandLogger.Log("=== LONG CONNECTION TEST START (5 min) ===");

        try
        {
            // Phase 1: navigation start via plugin flow → starts the PING
            // keepalive (15 s interval) exactly like a real ride. Real GPS
            // coordinates so the DEST frame on the display is not 0/0.
            double? startLat = Gps.LastReading?.Position.Latitude;
            double? startLon = Gps.LastReading?.Position.Longitude;
            NavigationStartInput startInput = new()
            {
                TotalDistanceKm = 12.5,
                TotalTimeMin = 8,
                StartLatitude = startLat,
                StartLongitude = startLon
            };
            await BleManager.ExecuteNavigationActionAsync("SendNavigationStartAsync", startInput);
            await Task.Delay(500);

            // Phases 2..10: one maneuver every 30 s, distance shrinking.
            // Keeps the display alive and lets us check if the connection
            // survives (keepalive + reconnect logic) with the screen off.
            (string Icon, string Instruction, string Street)[] steps =
            [
                ("turn-left",  "Links abbiegen\nHauptstraße", "Hauptstraße"),
                ("straight",   "Geradeaus fahren", "B 27"),
                ("turn-right", "Rechts abbiegen\nNebenstraße", "Nebenstraße"),
                ("roundabout-right-1", "Kreisverkehr\nAusfahrt 1", "L 1015"),
                ("turn-left",  "Links abbiegen\nSchwabstraße", "Schwabstraße"),
                ("straight",   "Geradeaus fahren", "L 1015"),
                ("turn-right", "Rechts abbiegen\nIndustriestraße", "Industriestraße"),
                ("roundabout-right-2", "Kreisverkehr\nAusfahrt 2", "K 1234"),
                ("turn-left",  "Links abbiegen\nZielstraße", "Zielstraße"),
                ("straight",   "Ziel erreicht in Kürze", "Ankunft")
            ];

            for (int i = 0; i < steps.Length; i++)
            {
                _longTestStep = i + 1;
                BleCommandLogger.Log($"LONG TEST step {_longTestStep}/{steps.Length}: {steps[i].Icon}");

                await BleManager.SendCommandAsync(Commands.NAVI, steps[i].Icon, steps[i].Instruction, steps[i].Street);
                await Task.Delay(200);

                double remainingKm = 12.5 * (1.0 - (double)i / steps.Length);
                double distToTurn = Math.Max(0, 400 - i * 40);
                await BleManager.SendCommandAsync(Commands.SM, "0", ((int)(remainingKm * 1000)).ToString(), ((int)distToTurn).ToString());
                await Task.Delay(200);

                // Countdown for turn maneuvers (like the old rapid test).
                if (steps[i].Icon.StartsWith("turn") || steps[i].Icon.StartsWith("roundabout"))
                {
                    await BleManager.SendCommandAsync(Commands.SM1, steps[i].Icon.Contains("left") ? "902" : "901", "7", "");
                }

                await InvokeAsync(StateHasChanged);

                // Wait for the next step, unless it was the last one.
                if (i < steps.Length - 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(LongTestStepDelaySec), _longTestCts.Token);
                }
            }

            // Phase 11: finish via plugin flow → stops the keepalive.
            await BleManager.ExecuteNavigationFinishAsync();
            BleCommandLogger.Log("=== LONG CONNECTION TEST END (finished) ===");
            Snackbar.Add(L["LongTestDone"], Severity.Success);
        }
        catch (OperationCanceledException)
        {
            BleCommandLogger.Log("=== LONG CONNECTION TEST STOPPED (cancelled) ===");
            Snackbar.Add(L["LongTestStopped"], Severity.Info);
        }
        catch (Exception ex)
        {
            BleCommandLogger.Log($"=== LONG CONNECTION TEST FAILED: {ex.Message} ===");
            Snackbar.Add(string.Format(L["LongTestError"], ex.Message), Severity.Error);
        }
        finally
        {
            _longTestRunning = false;
            _longTestCts?.Dispose();
            _longTestCts = null;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void StopLongTest()
    {
        _longTestCts?.Cancel();
    }

    // ── Cleanup ───────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _stateSubscription?.Dispose();
        _devicesSubscription?.Dispose();
        _errorSubscription?.Dispose();
    }
}
