using System.Text;
using Serilog;
using VegaBridgeApp.Models.BLE;

// ReSharper disable InvalidXmlDocComment

namespace VegaBridgeApp.Services.BLE.Plugins;

/// <summary>
/// MV Agusta BLE plugin – implements the protocol for MV Agusta motorcycles.
/// 
/// Protocol details (from decompiled APK + packet capture):
///   Service UUID: 00003719-0000-1000-8000-00805f9b34fb
///   Write char:   00002345-0000-1000-8000-00805f9b34fb  (Write-with-Response for Auth/Keepalive)
///   Read char:    00001234-0000-1000-8000-00805f9b34fb  (Notify for bike→phone)
/// 
/// Frame format: \r<CMD>\x1E<field1>\x1E<field2>...\r
/// </summary>
public class MvAgustaBlePlugin : IBleDevicePlugin
{
    private const byte Cr = 0x0D;
    private const byte Rs = 0x1E;

    public string ManufacturerId => "MVAGUSTA";
    public string DisplayName => "MV Agusta";
    public string BrandName => "MV AGUSTA";
    public Guid ServiceUuid => Guid.Parse("00003719-0000-1000-8000-00805f9b34fb");
    public string ControlWriteCharacteristicUuid => "00002345-0000-1000-8000-00805f9b34fb";
    public string ReadCharacteristicUuid => "00001234-0000-1000-8000-00805f9b34fb";

    public bool IsCompatible(BleDeviceInfo device)
    {
        // MV Agusta devices typically have "MV" or "BRUTALE" in their name.
        return device.Name.Contains("MV", StringComparison.OrdinalIgnoreCase) || 
               device.Name.Contains("BRUTALE", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SendAsync(IBleConnectedDevice device, string command, params string[] fields)
    {
        byte[] frame = BuildFrame(command, fields);
        // Use Write-with-Response for this plugin as a default for reliability
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: true);
    }

    public async Task SendTestAsync(IBleConnectedDevice device)
    {
        // Test frame: sends FINISH (destination reached) to verify BLE connectivity.
        byte[] frame = BuildFrame("FINISH", "", "", "");
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: true);
    }

    public void OnDataReceived(byte[] data)
    {
        if (TryParseFrame(data, out string command, out string[] fields))
        {
            // Logic to handle the parsed frame
            // In a real scenario, this might trigger an event or update a state machine.
            Log.Debug("MV Agusta Frame Received: {Command}, Fields: {Fields}", command, string.Join(", ", fields));
        }
    }

    // Internal protocol helpers
    private byte[] BuildFrame(string command, params string[] fields)
    {
        using MemoryStream ms = new();
        ms.WriteByte(Cr);
        ms.Write(Encoding.UTF8.GetBytes(command));

        foreach (var field in fields)
        {
            ms.WriteByte(Rs);
            byte[] fieldBytes = field != null
                ? Encoding.UTF8.GetBytes(field)
                : [];
            ms.Write(fieldBytes);
        }

        ms.WriteByte(Cr);
        return ms.ToArray();
    }

    private bool IsValidFrame(byte[] data)
    {
        return data.Length >= 3 && data[0] == Cr && data[^1] == Cr;
    }

    private bool TryParseFrame(byte[] data, out string command, out string[] fields)
    {
        command = string.Empty;
        fields = [];

        if (!IsValidFrame(data))
            return false;

        byte[] body = data[1..^1];

        if (body.Length == 0)
            return false;

        byte[][] parts = SplitByRs(body);

        if (parts.Length == 0)
            return false;

        command = Encoding.UTF8.GetString(parts[0]);
        fields = parts.Length > 1
            ? parts[1..].Select(b => Encoding.UTF8.GetString(b)).ToArray()
            : [];

        return true;
    }

    private byte[][] SplitByRs(byte[] data)
    {
        List<byte[]> result = [];
        int start = 0;
        for (int i = 0; i <= data.Length; i++)
        {
            if (i != data.Length && data[i] != Rs) continue;
            byte[] segment = new byte[i - start];
            Array.Copy(data, start, segment, 0, segment.Length);
            result.Add(segment);
            start = i + 1;
        }
        return [.. result];
    }
}
