namespace VegaBridgeApp.Models.BLE;

/// <summary>
/// Represents the BLE connection state for the UI.
/// </summary>
public enum BleConnectionState
{
    Idle,
    Scanning,
    Connecting,
    /// <summary>Link is up, the plugin is setting up the session (subscriptions, handshake).</summary>
    Initializing,
    /// <summary>Session is ready: the device accepts navigation frames.</summary>
    Connected,
    Error
}
