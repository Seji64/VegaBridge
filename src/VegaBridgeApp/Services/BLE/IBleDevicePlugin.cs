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
    /// GATT Service UUID as a string.
    /// </summary>
    string ServiceUuid { get; }

    /// <summary>
    /// GATT characteristic UUID for writing data.
    /// </summary>
    string WriteCharacteristicUuid { get; }

    /// <summary>
    /// GATT characteristic UUID for reading / subscribing to notifications.
    /// </summary>
    string ReadCharacteristicUuid { get; }

    /// <summary>
    /// If true, writes use Write-with-Response (for Auth/Keepalive).
    /// If false, writes use Write Command (fire-and-forget).
    /// </summary>
    bool RequiresWriteWithResponse { get; }

    /// <summary>
    /// Builds a raw frame byte array from a command and its fields.
    /// Frame format follows the manufacturer's protocol (e.g. \rCMD\x1Efields\r).
    /// </summary>
    byte[] BuildFrame(string command, params string[] fields);

    /// <summary>
    /// Attempts to parse a received data buffer into a command and field array.
    /// Returns true on success.
    /// </summary>
    bool TryParseFrame(byte[] data, out string command, out string[] fields);

    /// <summary>
    /// Creates a simple test frame for manual transmission testing.
    /// Typically a FINISH or benign command to verify BLE connectivity.
    /// </summary>
    byte[] CreateTestFrame();
}