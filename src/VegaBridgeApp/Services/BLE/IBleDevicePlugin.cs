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
}
