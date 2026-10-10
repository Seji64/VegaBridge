using VegaBridgeApp.Models.BLE;

namespace VegaBridgeApp.Services.BLE;

/// <summary>
/// Plugin interface for manufacturer-specific BLE communication.
/// Each manufacturer (MV Agusta, KTM, etc.) implements this interface.
/// </summary>
public interface IBleDevicePlugin
{
    /// <summary>
    /// Human-readable display name (e.g. "MV Agusta").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Short brand name for UI labels (e.g. "MV AGUSTA").
    /// </summary>
    string BrandName { get; }

    /// <summary>
    /// Determines if this plugin is compatible with the given device.
    /// </summary>
    bool IsCompatible(BleDeviceInfo device);

    /// <summary>
    /// GATT service UUID of the device's protocol.
    /// </summary>
    Guid ServiceUuid { get; }

    /// <summary>
    /// GATT characteristic UUID for reading / subscribing to notifications.
    /// </summary>
    string ReadCharacteristicUuid { get; }

    /// <summary>
    /// Creates and sends a simple test frame to verify BLE connectivity.
    /// </summary>
    Task SendTestAsync(IBleConnectedDevice device);

    /// <summary>
    /// Handles incoming data buffers received from the device.
    /// </summary>
    void OnDataReceived(byte[] data);

    // ─── Semantic Navigation Methods ─────────────────────────────────────

    /// <summary>
    /// Sends navigation start sequence to the device.
    /// Called once when navigation begins.
    /// </summary>
    Task SendNavigationStartAsync(IBleConnectedDevice device, NavigationStartInput input);

    /// <summary>
    /// Sends the full navigation state (instruction + distances) to the device.
    /// Called on every ~1 Hz status tick: the official MV Ride app re-sends the
    /// complete state each second, so a dropped frame is healed by the next tick.
    /// </summary>
    Task SendNavigationUpdateAsync(IBleConnectedDevice device, NavigationUpdateInput input);

    /// <summary>
    /// Sends navigation finish sequence to the device.
    /// Called when destination is reached.
    /// </summary>
    Task SendNavigationFinishAsync(IBleConnectedDevice device);

    /// <summary>
    /// Sends navigation stop command to the device.
    /// Called when user manually stops/cancels navigation.
    /// For MV Agusta, this is the same as FINISH (no separate STOP command exists).
    /// </summary>
    Task SendNavigationStopAsync(IBleConnectedDevice device);

    /// <summary>
    /// Sends an off-route alert to the device (MV Agusta: RENAVI, no
    /// payload – the bike reroutes on the command alone). Detection
    /// details are logged by the caller, not part of the frame.
    /// </summary>
    Task SendOffRouteAlertAsync(IBleConnectedDevice device);

    /// <summary>
    /// Sends the manufacturer's keepalive frame while a navigation session
    /// is active (MV Agusta: PING, the official MV Ride keepalive mechanism).
    /// The send cadence (15 s tick, 5 s skip after NAVI writes) is owned by
    /// the navigation coordinator, not by the plugin – the plugin only knows
    /// the frame itself. No-op if the brand has no keepalive mechanism.
    /// </summary>
    Task SendKeepAliveAsync(IBleConnectedDevice device);
}
