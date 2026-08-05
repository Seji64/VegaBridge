using System.Text;
using Serilog;
using VegaBridgeApp.Models.BLE;
using VegaBridgeApp.Models.BLE.MvAgusta;

// ReSharper disable InvalidXmlDocComment

namespace VegaBridgeApp.Services.BLE.Plugins;

/// <summary>
/// MV Agusta BLE plugin – implements the protocol for MV Agusta motorcycles.
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
        byte[] frame = BuildFrame(Commands.FINISH, "", "", "");
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: true);
    }

    // ─── Semantic Navigation Implementation ──────────────────────────────

    public async Task SendNavigationStartAsync(IBleConnectedDevice device, NavigationStartInput input)
    {
        Log.Debug("MV Agusta: Navigation Start - {Distance:F1}km, {Time:F0}min", input.TotalDistanceKm, input.TotalTimeMin);
        
        // Send initial NAVI frame with first maneuver (if available)
        if (input.UpcomingManeuvers?.Count > 0)
        {
            NavigationUpdateInput first = input.UpcomingManeuvers[0];
            await SendNavigationUpdateAsync(device, first with { IsFinal = false });
        }
        
        // Send initial status frame
        await SendStatusFrameAsync(device, 0, input.TotalDistanceKm * 1000, 0);
    }

    public async Task SendNavigationUpdateAsync(IBleConnectedDevice device, NavigationUpdateInput input)
    {
        Log.Debug("MV Agusta: Navigation Update - Maneuver {Index}/{Total}: {Icon}, Dist: {Dist:F0}m, Speed: {Speed:F0}km/h", 
            input.CurrentManeuverIndex + 1, input.TotalManeuvers, input.ManeuverIcon, input.DistanceToTurnM, input.SpeedKmh);

        // Send NAVI frame with maneuver info
        byte[] naviFrame = BuildFrame(Commands.NAVI, 
            input.ManeuverIcon, 
            input.InstructionText, 
            input.StreetName);
        await device.WriteAsync(ControlWriteCharacteristicUuid, naviFrame, withResponse: true);

        // Send SM (Status/Motion) frame with current metrics
        await SendStatusFrameAsync(device, input.SpeedKmh, input.RemainingDistanceKm * 1000, input.DistanceToTurnM);
    }

    public async Task SendNavigationFinishAsync(IBleConnectedDevice device)
    {
        Log.Debug("MV Agusta: Navigation Finish");
        byte[] frame = BuildFrame(Commands.FINISH, "", "", "");
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: true);
    }

    public async Task SendOffRouteAlertAsync(IBleConnectedDevice device, OffRouteAlertInput input)
    {
        Log.Warning("MV Agusta: Off-Route Alert - {Dist:F1}m at {Lat},{Lon}", input.DistanceMeters, input.Latitude, input.Longitude);
        
        // Signal the motorcycle that the route is being recalculated
        byte[] frame = BuildFrame(Commands.RENAVI, "off-route", "OFF ROUTE", $"{input.DistanceMeters:F0}m");
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: true);
    }

    // ─── Incoming Data Handling ──────────────────────────────────────────

    public void OnDataReceived(byte[] data)
    {
        if (TryParseFrame(data, out string command, out string[] fields))
        {
            // Logic to handle the parsed frame
            // In a real scenario, this might trigger an event or update a state machine.
            Log.Debug("MV Agusta Frame Received: {Command}, Fields: {Fields}", command, string.Join(", ", fields));
        }
    }

    // ─── Internal Protocol Helpers ───────────────────────────────────────

    private async Task SendStatusFrameAsync(IBleConnectedDevice device, double speedKmh, double remainingDistanceM, double distanceToTurnM)
    {
        byte[] smFrame = BuildFrame(Commands.SM,
            speedKmh.ToString("F0"),
            remainingDistanceM.ToString("F0"),
            distanceToTurnM.ToString("F0"));
        await device.WriteAsync(ControlWriteCharacteristicUuid, smFrame, withResponse: true);
    }

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

    // ─── Valhalla > MV Agusta Icon Mapping ─────────────────────────────
    // Moved here from NavigationService to keep protocol details in the plugin.

    private static readonly Dictionary<int, string> ValhallaToMvAgustaIcon = new()
    {
        { 1, TurnTypes.TurnRight },
        { 2, TurnTypes.TurnLeft },
        { 3, TurnTypes.Straight },
        { 4, TurnTypes.TurnSlightRight },
        { 5, TurnTypes.TurnSlightLeft },
        { 6, TurnTypes.TurnSlightRight },
        { 7, TurnTypes.TurnSlightLeft },
        { 8, TurnTypes.Straight },
        { 9, TurnTypes.TurnSlightRight },
        { 10, TurnTypes.TurnSlightLeft },
        { 11, TurnTypes.Straight },
        { 12, TurnTypes.Straight },
        { 13, TurnTypes.RoundaboutRight1 },
        { 14, TurnTypes.RoundaboutLeft1 },
        { 15, TurnTypes.Finish },
        { 16, TurnTypes.Finish }
    };
}
