using VegaBridgeApp.Models.Ble;

namespace VegaBridgeApp.Services.BLE;

/// <summary>
/// MV Agusta BLE plugin – implements the protocol for MV Agusta motorcycles.
/// 
/// Protocol details (from decompiled APK + packet capture):
///   Service UUID: 00003719-0000-1000-8000-00805f9b34fb
///   Write char:   00002345-0000-1000-8000-00805f9b34fb  (Write-with-Response for Auth/Keepalive)
///   Read char:    00001234-0000-1000-8000-00805f9b34fb  (Notify for bike→phone)
/// 
/// Frame format: \r<CMD>\x1E<field1>\x1E<field2>...\r
/// All 16 commands: HELLO, GUI1, VER, GPS, IOV, NEED, ORIG, DEST, REM,
///                  NAVI, SM, SM1, RENAVI, FINISH, G, MSG
/// </summary>
public class MvAgustaBlePlugin : IBleDevicePlugin
{
    public string ManufacturerId => "MVAGUSTA";
    public string DisplayName => "MV Agusta";
    public string ServiceUuid => "00003719-0000-1000-8000-00805f9b34fb";
    public string WriteCharacteristicUuid => "00002345-0000-1000-8000-00805f9b34fb";
    public string ReadCharacteristicUuid => "00001234-0000-1000-8000-00805f9b34fb";
    public bool RequiresWriteWithResponse => true;

    public byte[] BuildFrame(string command, params string[] fields)
        => MvAgustaFrameSerializer.BuildFrame(command, fields);

    public bool TryParseFrame(byte[] data, out string command, out string[] fields)
        => MvAgustaFrameSerializer.TryParseFrame(data, out command, out fields);

    // ── Convenience builders ──────────────────────────────────────────────

    /// <summary>HELLO handshake: \rHELLO\x1EA\x1E<manufacturer>\x1E<mac>\r</summary>
    public byte[] Hello(string manufacturer, string macAddress)
        => BuildFrame(MvAgustaCommands.HELLO, "A", manufacturer, macAddress);

    /// <summary>GUI1 keepalive: \rGUI1\x1E<sessionIdHex>\r</summary>
    public byte[] Gui1(string sessionIdHex)
        => BuildFrame(MvAgustaCommands.GUI1, sessionIdHex);

    /// <summary>VER version: \rVER\x1E<version>\r</summary>
    public byte[] Ver(string version)
        => BuildFrame(MvAgustaCommands.VER, version);

    /// <summary>GPS full position: \rGPS\x1E<lat>\x1E<lon>\x1E<speedMs>\x1E<heading>\r</summary>
    public byte[] Gps(double lat, double lon, double speedMs, double heading)
        => BuildFrame(MvAgustaCommands.GPS,
            lat.ToString("F6"),
            lon.ToString("F6"),
            speedMs.ToString("F1"),
            heading.ToString("F1"));

    /// <summary>IOV WiFi config: \rIOV\x1EWIFI_HOTSPOT\x1E<ssid>\x1E<password>\r</summary>
    public byte[] Iov(string ssid, string password)
        => BuildFrame(MvAgustaCommands.IOV, "WIFI_HOTSPOT", ssid, password);

    /// <summary>ORIG route start: \rORIG\x1E<address>\x1E<lat>\x1E<lon>\r</summary>
    public byte[] Orig(string address, double lat, double lon)
        => BuildFrame(MvAgustaCommands.ORIG, address, lat.ToString("F6"), lon.ToString("F6"));

    /// <summary>DEST route destination: \rDEST\x1E<address>\x1E<lat>\x1E<lon>\r</summary>
    public byte[] Dest(string address, double lat, double lon)
        => BuildFrame(MvAgustaCommands.DEST, address, lat.ToString("F6"), lon.ToString("F6"));

    /// <summary>REM remaining distance: \rREM\x1E\x1E<meters>\r</summary>
    public byte[] Rem(int meters)
        => BuildFrame(MvAgustaCommands.REM, "", meters.ToString());

    /// <summary>NAVI turn instruction: \rNAVI\x1E<icon>\x1E<instruction>\x1E<street>\r</summary>
    public byte[] Navi(string icon, string instruction, string street)
    {
        string instr = instruction.Length > 60 ? instruction[..60] : instruction;
        string str = street.Length > 60 ? street[..60] : street;
        return BuildFrame(MvAgustaCommands.NAVI, icon, instr, str);
    }

    /// <summary>SM speed & distances: \rSM\x1E<speedKmh>\x1E<destRemainM>\x1E<turnRemainM>\r</summary>
    public byte[] Sm(int speedKmh, int destRemainMeters, int turnRemainMeters)
        => BuildFrame(MvAgustaCommands.SM,
            speedKmh.ToString(),
            destRemainMeters.ToString(),
            turnRemainMeters.ToString());

    /// <summary>SM1 ETA / navigation status: \rSM1\x1E<routeInfo>\x1E<step>\r</summary>
    public byte[] Sm1(int routeInfo, int stepOrMinutes)
        => BuildFrame(MvAgustaCommands.SM1, routeInfo.ToString(), stepOrMinutes.ToString());

    /// <summary>RENAVI reroute: \rRENAVI\x1E\x1E\x1E\r</summary>
    public byte[] Renavi()
        => BuildFrame(MvAgustaCommands.RENAVI, "", "", "");

    /// <summary>FINISH navigation end: \rFINISH\x1E\x1E\x1E\r</summary>
    public byte[] Finish()
        => BuildFrame(MvAgustaCommands.FINISH, "", "", "");

    /// <summary>G partial GPS: \rG\x1E<lat>\x1E<lon>\x1E<timestamp>\r</summary>
    public byte[] G(double lat, double lon, long timestampUnixMs)
        => BuildFrame(MvAgustaCommands.G,
            lat.ToString("F6"),
            lon.ToString("F6"),
            timestampUnixMs.ToString());

    /// <summary>MSG notification: \rMSG\x1E<appId>\x1E<message>\x1E<title>\r</summary>
    public byte[] Msg(string appId, string message, string title)
        => BuildFrame(MvAgustaCommands.MSG, appId, message, title);

    /// <summary>NEED request (bike→phone): \rNEED\x1E\x1E\x1E\x1E\r</summary>
    public byte[] Need()
        => BuildFrame(MvAgustaCommands.NEED, "", "", "", "");

    /// <summary>Test frame: sends FINISH (destination reached) to verify BLE connectivity.</summary>
    public byte[] CreateTestFrame()
        => Finish();
}