using System.Text;

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
    public Guid ServiceUuid => Guid.Parse("00003719-0000-1000-8000-00805f9b34fb");
    public string ControlWriteCharacteristicUuid => "00002345-0000-1000-8000-00805f9b34fb";
    public string ReadCharacteristicUuid => "00001234-0000-1000-8000-00805f9b34fb";
    public bool RequiresWriteWithResponse => true;

    public byte[] BuildFrame(string command, params string[] fields)
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

    public bool IsValidFrame(byte[] data)
    {
        return data.Length >= 3 && data[0] == Cr && data[^1] == Cr;
    }

    public bool TryParseFrame(byte[] data, out string command, out string[] fields)
    {
        command = string.Empty;
        fields = [];

        if (!IsValidFrame(data))
            return false;

        // Strip CR framing
        byte[] body = data[1..^1];

        if (body.Length == 0)
            return false;

        // Split by RS (0x1E)
        byte[][] parts = SplitByRs(body);

        if (parts.Length == 0)
            return false;

        command = Encoding.UTF8.GetString(parts[0]);
        fields = parts.Length > 1
            ? parts[1..].Select(b => Encoding.UTF8.GetString(b)).ToArray()
            : Array.Empty<string>();

        return true;
    }

    private byte[][] SplitByRs(byte[] data)
    {
        List<byte[]> result = new();
        int start = 0;
        for (int i = 0; i <= data.Length; i++)
        {
            if (i != data.Length && data[i] != Rs) continue;
            byte[] segment = new byte[i - start];
            Array.Copy(data, start, segment, 0, segment.Length);
            result.Add(segment);
            start = i + 1;
        }
        return result.ToArray();
    }

    /// <summary>Test frame: sends FINISH (destination reached) to verify BLE connectivity.</summary>
    public byte[] CreateTestFrame()
    {
        return BuildFrame("FINISH", "", "", "");
    }

    public async Task<bool> SendAsync(object device, byte[] data, bool isControlFrame)
    {
        /*try
        {
            IBluetoothRemoteService? service = device.GetService(ServiceUuid);
            if (service == null) return false;

            IBluetoothRemoteCharacteristic? characteristic = service.GetCharacteristicOrDefault(Guid.Parse(ControlWriteCharacteristicUuid));
            if (characteristic == null) return false;

            bool useResponse = isControlFrame && RequiresWriteWithResponse;
            
            await characteristic.WriteValueAsync(data, useResponse, TimeSpan.FromSeconds(10));
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "MvAgustaBlePlugin failed to send data");
            return false;
        }*/
        return true;
    }
}