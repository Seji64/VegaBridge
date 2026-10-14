namespace VegaBridgeApp.Services.BLE;

/// <summary>
/// Hardware Abstraction Layer (HAL) for one link session with a connected BLE
/// device. Prevents plugins from depending directly on the underlying BLE
/// stack (e.g., Shiny).
///
/// A session lives from link-up to link loss: once it ends, its
/// subscriptions stop delivering data and writes throw. The next link-up
/// hands the plugin a fresh session via
/// <see cref="IBleDevicePlugin.InitializeSessionAsync"/>.
/// </summary>
public interface IBleConnectedDevice
{
    /// <summary>
    /// Stable platform identifier of the peripheral (Apple: the CoreBluetooth
    /// UUID), e.g. the key for per-device data such as pairing keys.
    /// </summary>
    string DeviceId { get; }

    /// <summary>
    /// Writes data to a GATT characteristic.
    /// </summary>
    /// <param name="serviceUuid">The service owning the characteristic.</param>
    /// <param name="characteristicUuid">The UUID of the characteristic to write to.</param>
    /// <param name="data">The raw byte array to send.</param>
    /// <param name="withResponse">false: write command (the MV Agusta display
    /// accepts only these). true: acknowledged write – the task completes on
    /// the device's acknowledgement, and payloads beyond the MTU are sent as
    /// a long write.</param>
    Task WriteAsync(Guid serviceUuid, string characteristicUuid, byte[] data, bool withResponse);

    /// <summary>
    /// Subscribes to the notifications or indications of a characteristic for
    /// the rest of this session; the subscription ends with the session.
    /// </summary>
    /// <param name="serviceUuid">The service owning the characteristic.</param>
    /// <param name="characteristicUuid">The UUID of the characteristic to subscribe to.</param>
    /// <param name="onData">Receives each payload. Exceptions are logged, not propagated.</param>
    void Subscribe(Guid serviceUuid, string characteristicUuid, Action<byte[]> onData);
}
