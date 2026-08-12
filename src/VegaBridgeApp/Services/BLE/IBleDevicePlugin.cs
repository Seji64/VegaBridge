using VegaBridgeApp.Models.BLE;

namespace VegaBridgeApp.Services.BLE;

/// <summary>
/// Plugin interface for manufacturer-specific BLE communication.
/// Each manufacturer (MV Agusta, KTM, etc.) implements this interface.
/// </summary>
public interface IBleDevicePlugin
{
    /// <summary>
    /// Unique manufacturer identifier (e.g. "MVAGUSTA", "KTM").
    /// </summary>
    string ManufacturerId { get; }

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
    /// GATT Service UUID as a string.
    /// </summary>
    Guid ServiceUuid { get; }

    /// <summary>
    /// GATT characteristic UUID for writing control data.
    /// </summary>
    string ControlWriteCharacteristicUuid { get; }

    /// <summary>
    /// GATT characteristic UUID for reading / subscribing to notifications.
    /// </summary>
    string ReadCharacteristicUuid { get; }

    /// <summary>
    /// Sends a command to the device using the manufacturer-specific logic.
    /// Kept for raw/test access.
    /// </summary>
    Task SendAsync(IBleConnectedDevice device, string command, params string[] fields);

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
    /// Sends a navigation update (maneuver, speed, distance) to the device.
    /// Called on maneuver change and periodically (throttled) for status updates.
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
    /// Sends an off-route alert to the device.
    /// </summary>
    Task SendOffRouteAlertAsync(IBleConnectedDevice device, OffRouteAlertInput input);
}
