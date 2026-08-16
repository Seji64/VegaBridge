using Microsoft.Extensions.DependencyInjection;
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
        Window window = new(new MainPage()) { Title = "VegaBridgeApp" };

        // iOS silently drops the BLE link while the app is in the background
        // (screen off, phone in the pocket) – often without a disconnect
        // event, so the UI keeps showing "Connected" while writes time out.
        // When the app returns to the foreground, verify the connection and
        // rebuild it if needed, then re-send the current navigation state so
        // the bike display does not stay on stale instructions.
        window.Resumed += async (_, _) =>
        {
            try
            {
                if (_services == null) return;

                BleManagerService ble = _services.GetRequiredService<BleManagerService>();
                bool connected = await ble.EnsureConnectedAsync();
                Log.Information("App resumed – BLE connection {State}", connected ? "alive" : "unavailable");

                if (connected)
                {
                    BleNavigationCoordinator coordinator =
                        _services.GetRequiredService<BleNavigationCoordinator>();
                    await coordinator.ResendCurrentStateAsync();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "App resume BLE reconnect failed");
            }
        };

        return window;
    }
}
