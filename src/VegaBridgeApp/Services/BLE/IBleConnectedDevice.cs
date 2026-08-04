namespace VegaBridgeApp.Services.BLE;

/// <summary>
/// Hardware Abstraction Layer (HAL) for a connected BLE device.
/// Prevents plugins from depending directly on the underlying BLE stack (e.g., Shiny).
/// </summary>
public interface IBleConnectedDevice
{
    /// <summary>
    /// Unique identifier of the device.
    /// </summary>
    Guid Uuid { get; }

    /// <summary>
    /// Human-readable name of the device.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Writes data to a specific GATT characteristic.
    /// </summary>
    /// <param name="characteristicUuid">The UUID of the characteristic to write to.</param>
    /// <param name="data">The raw byte array to send.</param>
    /// <param name="withResponse">If true, waits for a response from the device.</param>
    /// <returns>Task representing the asynchronous operation.</returns>
    Task WriteAsync(string characteristicUuid, byte[] data, bool withResponse);

    /// <summary>
    /// Reads data from a specific GATT characteristic.
    /// </summary>
    /// <param name="characteristicUuid">The UUID of the characteristic to read.</param>
    /// <returns>The raw byte array read from the device.</returns>
    Task<byte[]?> ReadAsync(string characteristicUuid);
}
