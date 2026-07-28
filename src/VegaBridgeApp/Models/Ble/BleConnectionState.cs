namespace VegaBridgeApp.Models.Ble;

/// <summary>
/// Represents the BLE connection state for the UI.
/// </summary>
public enum BleConnectionState
{
    Unknown,
    NoBle,
    Scanning,
    FoundDevices,
    Connecting,
    Connected,
    Disconnecting,
    Disconnected,
    Error
}