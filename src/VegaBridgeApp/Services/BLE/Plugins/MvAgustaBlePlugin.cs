using System.Globalization;
using System.Text;
using Serilog;
using VegaBridgeApp.Models.BLE;
using VegaBridgeApp.Models.BLE.MvAgusta;
using VegaBridgeApp.Models.Navigation;

// ReSharper disable InvalidXmlDocComment

namespace VegaBridgeApp.Services.BLE.Plugins;

/// <summary>
/// MV Agusta BLE plugin – implements the protocol for MV Agusta motorcycles.
/// </summary>
public class MvAgustaBlePlugin : IBleDevicePlugin
{
    public string DisplayName => "MV Agusta";
    public string BrandName => "MV AGUSTA";
    public Guid ServiceUuid => Guid.Parse("00003719-0000-1000-8000-00805f9b34fb");
    public string ReadCharacteristicUuid => "00001234-0000-1000-8000-00805f9b34fb";
    private const string WriteCharacteristicUuid = "00002345-0000-1000-8000-00805f9b34fb";

    // SM1 (arrival time) cadence of the official app: every 30 s.
    private static readonly TimeSpan Sm1Interval = TimeSpan.FromSeconds(30);
    private DateTimeOffset _lastSm1SentAt = DateTimeOffset.MinValue;

    // Valhalla puts the exit number only on the roundabout-ENTER maneuver;
    // the following roundabout-EXIT maneuver keeps showing the same icon.
    private string? _lastRoundaboutIcon;

    // MV Agusta devices typically have "MV" or "BRUTALE" in their name.
    public bool IsCompatible(BleDeviceInfo device) =>
        device.Name.Contains("MV", StringComparison.OrdinalIgnoreCase) ||
        device.Name.Contains("BRUTALE", StringComparison.OrdinalIgnoreCase);

    // Test frame: a WhatsApp-style MSG (⏎MSG⏝<appId>⏝<message>⏝<title>⏎) so the
    // user sees a readable message on the bike. "whatsapp" as appId mirrors the
    // real MV Ride app and guarantees a visible payload.
    public Task SendTestAsync(IBleConnectedDevice device) =>
        SendAsync(device, Commands.MSG, "whatsapp", "Test from VegaBridge", "VegaBridge");

    // ─── Semantic Navigation Implementation ──────────────────────────────

    public async Task SendNavigationStartAsync(IBleConnectedDevice device, NavigationStartInput input)
    {
        // DEST format (from pklg capture): DEST|\x1e|lon\x1e|lat\x1e|
        // Field 1 (address) is empty in the official MV Ride app.
        // Field 2 = longitude, field 3 = latitude (both 6 decimal places) of
        // the DESTINATION – identical in all 10 DEST frames of the capture,
        // across reroutes. 0/0 only for test sequences without a real route.
        string lon = (input.DestinationLongitude ?? 0).ToString("F6", CultureInfo.InvariantCulture);
        string lat = (input.DestinationLatitude ?? 0).ToString("F6", CultureInfo.InvariantCulture);
        // No manual pacing: W2R flow control is handled by the write wrapper
        // in BleManagerService.
        await SendAsync(device, Commands.DEST, "", lon, lat);

        // REM format (from pklg capture): REM|\x1e|<meters>\x1e|
        // 3 RS separators → 4 fields: command, empty, meters, empty
        await SendAsync(device, Commands.REM, "", (input.TotalDistanceKm * 1000).ToString("F0"), "");

        // New session (or reroute): send SM1 with the next update.
        _lastSm1SentAt = DateTimeOffset.MinValue;
        _lastRoundaboutIcon = null;
        // PING keepalive is deliberately NOT started here: the cadence is
        // owned by the navigation coordinator (15 s tick), which calls
        // SendKeepAliveAsync on the live connection.
    }

    public async Task SendNavigationUpdateAsync(IBleConnectedDevice device, NavigationUpdateInput input)
    {
        // NAVI format: NAVI|icon|navigationGuide|intersectionName
        // Per BluetoothService.java (mvride v1.4.3):
        // - navigationGuide = direction.getDescription() (e.g., "Turn left\nRosenstraße" in the bike's locale)
        // - intersectionName = direction.getRoadName() (e.g., "Rosenstraße")
        // - Both strings truncated to 60 chars by the official app
        // - Instruction ends with newline separator
        const int maxLen = 60;

        string navigationGuide = string.IsNullOrEmpty(input.InstructionText)
            ? ""
            : (input.InstructionText.EndsWith("\n", StringComparison.Ordinal)
                ? input.InstructionText
                : input.InstructionText + "\n");
        if (navigationGuide.Length > maxLen)
            navigationGuide = navigationGuide[..maxLen];

        string intersectionName = input.StreetName;
        if (intersectionName.Length > maxLen)
            intersectionName = intersectionName[..maxLen];

        // NAVI = the instruction frame the display shows. Written on EVERY
        // tick, like the official MV Ride app (289 NAVI+SM pairs in 5.7 min
        // of the capture): a frame lost to a W2R stall is healed by the next
        // tick instead of freezing the display until the next maneuver.
        // Flow control (buffer full → drop/force, watchdog) lives in the
        // write wrapper of BleManagerService.
        await SendAsync(device, Commands.NAVI,
            ToBikeIcon(input.ManeuverIcon, input.RoundaboutExitCount),
            navigationGuide,
            intersectionName);

        // SM and SM1 are non-critical (status display). If the BLE queue
        // is full after NAVI, skip them instead of throwing. NAVI is the
        // frame that shows the actual instruction on the bike display.
        try
        {
            // SM format: SM|speed_field|remainingDistanceM|distanceToTurnM
            // Field 1 is "0" in official captures.
            await SendAsync(device, Commands.SM,
                "0",
                (input.RemainingDistanceKm * 1000).ToString("F0"),
                input.DistanceToTurnM.ToString("F0"));
        }
        catch (Exception ex)
        {
            // SM failed (queue full or link flapping). Log it – otherwise the
            // bike's status display goes silently stale and the root cause is
            // invisible in the field.
            Log.Debug(ex, "SM frame failed – skipping");
        }

        // SM1 = arrival time + remaining time (MV Ride v1.4.3:
        // sendNavigationStatusEveryThirtySeconds), NOT a turn countdown –
        // the capture's "SM1|902|7" means "arrive 15:02, 7 min left".
        // The official app sends it every 30 s, so does this.
        if (DateTimeOffset.UtcNow - _lastSm1SentAt >= Sm1Interval)
        {
            try
            {
                await SendArrivalTimeAsync(device, input.RemainingTimeMin);
                // Marked only once delivered: a frame dropped by a full W2R
                // buffer is retried on the next tick.
                _lastSm1SentAt = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                // Non-critical frame. Log for field diagnostics.
                Log.Debug(ex, "SM1 frame failed – retry next tick");
            }
        }
        // End-of-tick marker (write latency = NAV ACTION → NAV UPDATE in the log)
        Log.Information("BLE-LOGGER: {Line}", $"NAV UPDATE: idx={input.CurrentManeuverIndex}, icon={input.ManeuverIcon}, dist={input.DistanceToTurnM:F0}m, speed={input.SpeedKmh:F0}km/h");
    }

    // Keepalive cadence is owned by the navigation coordinator, so there is
    // nothing to stop here – it observes the navigation-stop event.
    public Task SendNavigationFinishAsync(IBleConnectedDevice device) =>
        SendAsync(device, Commands.FINISH, "", "", "");

    // For MV Agusta, there is no separate STOP command – FINISH is used for
    // both destination reached and user-cancelled navigation (confirmed via
    // BLE trace analysis).
    public Task SendNavigationStopAsync(IBleConnectedDevice device) =>
        SendNavigationFinishAsync(device);

    // RENAVI format (from pklg capture): RENAVI|\x1e|\x1e|
    // All fields empty – the bike switches to rerouting mode based on the
    // command alone. Detection details are logged by the caller.
    public Task SendOffRouteAlertAsync(IBleConnectedDevice device) =>
        SendAsync(device, Commands.RENAVI, "", "", "");

    /// <summary>
    /// Sends a PING keepalive frame (official MV Ride keepalive mechanism).
    /// PING format: \rPING\u001E\u001E\u001E\r (4 fields, all empty after command).
    /// This is the ONLY PING logic in the plugin: the 15 s cadence, the 5 s
    /// post-NAVI skip and start/stop with the navigation session are owned
    /// by the BleNavigationCoordinator, which calls this on the live link.
    /// </summary>
    public Task SendKeepAliveAsync(IBleConnectedDevice device) =>
        SendAsync(device, Commands.PING, "", "", "");

    // ─── Incoming Data Handling ──────────────────────────────────────────

    public void OnDataReceived(byte[] data)
    {
        if (TryParseFrame(data, out string command, out string[] fields))
        {
            // GUI1 notification from the bike: log the session ID for
            // diagnostics only. The phone intentionally never writes GUI1
            // back – PING keepalive + NAVI/SM frames keep the session alive
            // (official MV Ride capture shows 0 GUI1 writes from the phone).
            Log.Information("BLE-LOGGER: {Line}",
                command == "GUI1" && fields.Length > 0
                    ? $"RECV GUI1 frame: {BitConverter.ToString(data)}, sessionId={fields[0]}"
                    : $"RECV {command} frame: {BitConverter.ToString(data)}");
            Log.Debug("MV Agusta Frame Received: {Command}, Fields: {Fields}", command, string.Join(", ", fields));
        }
        else
        {
            Log.Information("BLE-LOGGER: {Line}", $"RECV INVALID frame: {BitConverter.ToString(data)}");
        }
    }

    // ─── Internal Protocol Helpers ───────────────────────────────────────

    // Frame layout: 0x0D + command + (0x1E + field)* + 0x0D. Failures propagate
    // to BleManagerService, which logs them per frame type.
    private static Task SendAsync(IBleConnectedDevice device, string command, params string[] fields)
    {
        char cr = '\r', rs = '\u001E';
        byte[] frame = Encoding.UTF8.GetBytes($"{cr}{command}{string.Concat(fields.Select(f => $"{rs}{f}"))}{cr}");
        Log.Information("BLE-LOGGER: {Line}", $"SEND {command} frame: {BitConverter.ToString(frame)}");
        return device.WriteAsync(WriteCharacteristicUuid, frame);
    }

    private static bool TryParseFrame(byte[] data, out string command, out string[] fields)
    {
        command = string.Empty;
        fields = [];

        if (data.Length < 3 || data[0] != 0x0D || data[^1] != 0x0D)
            return false;

        string[] parts = Encoding.UTF8.GetString(data[1..^1]).Split('\u001E');
        command = parts[0];
        fields = parts.Length > 1 ? parts[1..] : [];
        return true;
    }

    // ─── Semantic icon > MV Agusta Icon Mapping ────────────────────────

    /// <summary>
    /// Maps the plugin-agnostic semantic icon to one of the 35 keys the bike
    /// knows (TurnByTurnIndication enum of MV Ride v1.4.3). Unknown keys are
    /// not displayed by the bike. The mapping follows the official app
    /// (HERE ManeuverAction → key): exits/ramps → slight turn (the bike has
    /// no exit icon), sharp turns → U-turn, roundabouts → roundabout-right-N
    /// (counter-clockwise, right-hand traffic), destination → "Finish".
    /// </summary>
    private string ToBikeIcon(string semanticIcon, int? roundaboutExitCount)
    {
        if (semanticIcon != NavigationIconMapper.IconRoundabout)
            _lastRoundaboutIcon = null;

        switch (semanticIcon)
        {
            case NavigationIconMapper.IconTurnLeft: return TurnTypes.TurnLeft;
            case NavigationIconMapper.IconTurnRight: return TurnTypes.TurnRight;
            case NavigationIconMapper.IconSlightLeft:
            case NavigationIconMapper.IconExitLeft: return TurnTypes.TurnSlightLeft;
            case NavigationIconMapper.IconSlightRight:
            case NavigationIconMapper.IconExitRight: return TurnTypes.TurnSlightRight;
            case NavigationIconMapper.IconSharpLeft:
            case NavigationIconMapper.IconUTurnLeft: return TurnTypes.UturnLeft;
            case NavigationIconMapper.IconSharpRight:
            case NavigationIconMapper.IconUTurnRight: return TurnTypes.UturnRight;
            case NavigationIconMapper.IconFinish: return TurnTypes.Finish;
            case NavigationIconMapper.IconRoundabout:
                if (roundaboutExitCount is > 0)
                    _lastRoundaboutIcon = $"roundabout-right-{Math.Min(roundaboutExitCount.Value, 12)}";
                return _lastRoundaboutIcon ?? TurnTypes.Straight;
            default: return TurnTypes.Straight;
        }
    }

    /// <summary>
    /// Sends SM1: arrival time as minutes since local midnight + remaining
    /// minutes, e.g. SM1|902|7 = arrive 15:02, 7 min left (MV Ride v1.4.3).
    /// </summary>
    private Task SendArrivalTimeAsync(IBleConnectedDevice device, double remainingTimeMin)
    {
        double remaining = Math.Max(0, remainingTimeMin);
        int remainingMin = (int)remaining;
        DateTime arrival = DateTime.Now.AddMinutes(remaining);
        int arrivalMinuteOfDay = arrival.Hour * 60 + arrival.Minute;
        return SendAsync(device, Commands.SM1,
            arrivalMinuteOfDay.ToString(CultureInfo.InvariantCulture),
            remainingMin.ToString(CultureInfo.InvariantCulture),
            "");
    }
}
