namespace VegaBridgeApp.Models.BLE;

/// <summary>
/// Represents the BLE connection state for the UI.
/// </summary>
public enum BleConnectionState
{
    Idle,
    Scanning,
    Connecting,
    Connected,
    Error
}