namespace VegaBridgeApp.Services.BLE;

/// <summary>
/// Hardware Abstraction Layer (HAL) for a connected BLE device.
/// Prevents plugins from depending directly on the underlying BLE stack (e.g., Shiny).
/// </summary>
public interface IBleConnectedDevice
{
    /// <summary>
    /// Writes data to a GATT characteristic without response – the only write
    /// type the supported displays accept.
    /// </summary>
    /// <param name="characteristicUuid">The UUID of the characteristic to write to.</param>
    /// <param name="data">The raw byte array to send.</param>
    Task WriteAsync(string characteristicUuid, byte[] data);
}
