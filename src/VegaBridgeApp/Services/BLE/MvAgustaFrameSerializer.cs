using System.Text;

namespace VegaBridgeApp.Services.BLE;

/// <summary>
/// Frame serializer for the MV Agusta BLE protocol.
/// Wire format:
///   <0x0D><CMD><0x1E><field1><0x1E><field2>...<0x0D>
/// where 0x0D = CR (Carriage Return), 0x1E = RS (Record Separator).
/// All fields are UTF-8 text.
/// </summary>
public static class MvAgustaFrameSerializer
{
    private const byte Cr = 0x0D;
    private const byte Rs = 0x1E;

    /// <summary>
    /// Builds a frame: \r<command>\x1E<field1>\x1E<field2>\r
    /// </summary>
    public static byte[] BuildFrame(string command, params string[] fields)
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

    /// <summary>
    /// Parses a received frame. Returns true on success.
    /// Expects: \r...\r framing.
    /// </summary>
    public static bool TryParseFrame(byte[] data, out string command, out string[] fields)
    {
        command = string.Empty;
        fields = [];

        if (data.Length < 3)
            return false; // Minimum: \rX\r

        if (data[0] != Cr || data[^1] != Cr)
            return false; // Must start/end with CR

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

    /// <summary>
    /// Checks if a frame is an MV Agusta command frame.
    /// </summary>
    public static bool IsValidFrame(byte[] data)
    {
        return data.Length >= 3 && data[0] == Cr && data[^1] == Cr;
    }

    // ── Helper splits ─────────────────────────────────────────────────────

    private static byte[][] SplitByRs(byte[] data)
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
}