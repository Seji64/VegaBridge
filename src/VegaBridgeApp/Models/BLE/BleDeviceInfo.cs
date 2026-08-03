namespace VegaBridgeApp.Models.BLE;

/// <summary>
/// Holds information about a discovered BLE peripheral.
/// </summary>
public class BleDeviceInfo
{
    public required string Name { get; init; }
    public required string Uuid { get; init; }
    public int Rssi { get; set; }
    public bool IsConnectable { get; init; }
    public bool IsConnected { get; set; }
    public bool IsPaired { get; init; }
    public DateTime FirstDiscovered { get; init; }
}