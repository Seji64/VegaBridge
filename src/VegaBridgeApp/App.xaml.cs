using Serilog;
using VegaBridgeApp.Services.BLE;

namespace VegaBridgeApp;

public partial class App : Application
{
    private IServiceProvider? _services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        Window window = new(new MainPage()) { Title = "VegaBridge" };

        // Shiny.BluetoothLE (≥ 5.6) owns link recovery: a dropped link or a
        // Bluetooth power cycle is reconnected by Shiny itself, and its
        // WhenConnected() hook (BleManagerService.SetupWhenConnected) re-runs
        // our per-connection setup. On app resume we only verify the status
        // (or auto-connect when no device is tracked at all) and re-send the
        // current navigation state so the bike display does not stay on
        // stale instructions. A user-initiated disconnect is never
        // overridden (intent flag in BleManagerService).
        // Timeline marker for display-off runs: from here the app is
        // backgrounded (and soon suspended) – anything logged before this
        // line happened while the app was active.
        // The event double-fires in bursts (~60 ms apart, scene-lifecycle
        // quirk observed in the live logs) – debounce so the timeline gets
        // one line per backgrounding, not a duplicate pair.
        DateTime lastDeactivatedAt = DateTime.MinValue;
        window.Deactivated += (_, _) =>
        {
            if ((DateTime.UtcNow - lastDeactivatedAt).TotalMilliseconds < 1000)
                return;
            lastDeactivatedAt = DateTime.UtcNow;
            Log.Information("App deactivated (backgrounded) – BLE keepalive continues in background, app may be suspended");
        };

        // Auto-connect to the last connected motorcycle (setting on the
        // Settings page). Swallows its own failures and is a no-op while a
        // device is tracked or after a user-initiated disconnect.
        window.Created += async (_, _) =>
        {
            if (_services == null) return;
            await _services.GetRequiredService<BleManagerService>().TryAutoConnectAsync();
        };

        window.Resumed += async (_, _) =>
        {
            try
            {
                if (_services == null) return;

                BleManagerService ble = _services.GetRequiredService<BleManagerService>();
                bool connected = await ble.EnsureConnectedAsync() || await ble.TryAutoConnectAsync();
                Log.Information("App resumed – BLE connection {State}", connected ? "alive" : "unavailable");

                if (!connected) return;
                BleNavigationCoordinator coordinator =
                    _services.GetRequiredService<BleNavigationCoordinator>();
                await coordinator.ResendCurrentStateAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "App resume BLE reconnect failed");
            }
        };

        return window;
    }
}
