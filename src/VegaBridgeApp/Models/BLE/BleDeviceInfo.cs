namespace VegaBridgeApp.Models.BLE;

/// <summary>
/// Holds information about a discovered BLE peripheral.
/// </summary>
public class BleDeviceInfo
{
    public required Guid Uuid { get; init; }
    public required string Name { get; init; }
    public string? Brand { get; set; }
    public bool IsConnected { get; set; }
    public DateTime LastSeen { get; set; }
}