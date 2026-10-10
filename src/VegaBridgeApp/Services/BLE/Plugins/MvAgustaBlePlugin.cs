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
    public string ManufacturerId => "MVAGUSTA";
    public string DisplayName => "MV Agusta";
    public string BrandName => "MV AGUSTA";
    public Guid ServiceUuid => Guid.Parse("00003719-0000-1000-8000-00805f9b34fb");
    public string ControlWriteCharacteristicUuid => "00002345-0000-1000-8000-00805f9b34fb";
    public string ReadCharacteristicUuid => "00001234-0000-1000-8000-00805f9b34fb";

    // On-change SM1 (same policy as NAVI): the last SM1 frame that was
    // actually written, keyed by maneuver|type|countdown. Null until the
    // first SM1 of a navigation session.
    private string? _lastSm1Key;

    public bool IsCompatible(BleDeviceInfo device)
    {
        // BleDeviceInfo.Name is declared `required string`, but OS-connected
        // peripherals can still surface with a null name before iOS has read
        // it (see UpdateDeviceList: Name = p.Name!). Defensive null-checks
        // are required at runtime despite the non-nullable declaration.
        // MV Agusta devices typically have "MV" or "BRUTALE" in their name.
        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
        return device.Name?.Contains("MV", StringComparison.OrdinalIgnoreCase) == true ||
               device.Name?.Contains("BRUTALE", StringComparison.OrdinalIgnoreCase) == true;
    }

    public async Task SendAsync(IBleConnectedDevice device, string command, params string[] fields)
    {
        try
        {
            byte[] frame = BuildFrame(command, fields);
            Log.Information("BLE-LOGGER: {Line}", $"SEND {command} frame: {BitConverter.ToString(frame)}");
            // Use Write-without-Response for this characteristic as the device does not support response writes
            await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: false);
        }
        catch (Exception ex)
        {
            // Shiny's write queue faults dead-link writes with BleException and
            // its auto-reconnect (Shiny.BluetoothLE ≥ 5.6) owns link recovery –
            // the plugin only logs; the next tick/GPS update retries.
            Log.Error(ex, "MV Agusta: SEND {Command} failed", command);
            throw;
        }
    }

    public async Task SendTestAsync(IBleConnectedDevice device)
    {
        // Test frame: sends a WhatsApp‑style MSG command so the user sees a readable message on the bike.
        // The MSG format is: ⏎MSG⏝<appId>⏝<message>⏝<title>⏎
        // Using "whatsapp" as the appId mirrors the real MV Ride app behaviour and guarantees a visible payload.
        byte[] frame;
        try
        {
            frame = BuildFrame(Commands.MSG, "whatsapp", "Test from VegaBridge", "VegaBridge");
            // Log the raw frame for debugging
            Log.Information("BLE-LOGGER: {Line}", $"SEND MSG frame: {BitConverter.ToString(frame)}");
            // Write‑without‑Response is fine for MSG – the bike only needs to display the payload.
            await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: false);
        }
        catch (Exception ex)
        {
            // Same failure path as SendAsync – see its catch.
            Log.Error(ex, "MV Agusta: SEND MSG (test) failed");
            throw;
        }
    }

    // ─── Semantic Navigation Implementation ──────────────────────────────

    public async Task SendNavigationStartAsync(IBleConnectedDevice device, NavigationStartInput input)
    {
        Log.Debug("MV Agusta: Navigation Start - {Distance:F1}km, {Time:F0}min", input.TotalDistanceKm, input.TotalTimeMin);
        Log.Information("BLE-LOGGER: {Line}", $"NAV START: distance={input.TotalDistanceKm:F1}km, time={input.TotalTimeMin:F0}min, maneuvers={input.UpcomingManeuvers?.Count ?? 0}");

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

        // New session: force the first SM1 of the first maneuver even if it
        // matches the last frame of a previous session.
        _lastSm1Key = null;
        // PING keepalive is deliberately NOT started here: the cadence is
        // owned by the navigation coordinator (15 s tick), which calls
        // SendKeepAliveAsync on the live connection.
    }

    public async Task SendNavigationUpdateAsync(IBleConnectedDevice device, NavigationUpdateInput input)
    {
        Log.Debug("MV Agusta: Navigation Update - Maneuver {Index}/{Total}: {Icon}, Dist: {Dist:F0}m, Speed: {Speed:F0}km/h", 
            input.CurrentManeuverIndex + 1, input.TotalManeuvers, input.ManeuverIcon, input.DistanceToTurnM, input.SpeedKmh);

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
        
        // Truncate to 60 chars as per official implementation
        if (navigationGuide.Length > maxLen)
            navigationGuide = navigationGuide[..maxLen];
        
        string intersectionName = string.IsNullOrEmpty(input.IntersectionName)
            ? (string.IsNullOrEmpty(input.StreetName) ? "" : input.StreetName)
            : input.IntersectionName;
        if (intersectionName.Length > maxLen)
            intersectionName = intersectionName[..maxLen];
        
        // NAVI = the instruction frame the display shows. Written on EVERY
        // tick, like the official MV Ride app (289 NAVI+SM pairs in 5.7 min
        // of the capture): a frame lost to a W2R stall is healed by the next
        // tick instead of freezing the display until the next maneuver.
        // Flow control (buffer full → drop/force, watchdog) lives in the
        // write wrapper of BleManagerService.
        byte[] naviFrame = BuildFrame(Commands.NAVI,
            input.ManeuverIcon,
            navigationGuide,
            intersectionName);
        Log.Information("BLE-LOGGER: {Line}", $"SEND NAVI frame: {BitConverter.ToString(naviFrame)}");
        await device.WriteAsync(ControlWriteCharacteristicUuid, naviFrame, withResponse: false);

        // SM and SM1 are non-critical (status display). If the BLE queue
        // is full after NAVI, skip them instead of throwing. NAVI is the
        // frame that shows the actual instruction on the bike display.
        try
        {
            await SendStatusFrameAsync(device, input.RemainingDistanceKm * 1000, input.DistanceToTurnM);
        }
        catch (Exception ex)
        {
            // SM failed (queue full or link flapping). Log it – otherwise the
            // bike's status display goes silently stale and the root cause is
            // invisible in the field.
            Log.Debug(ex, "SM frame failed – skipping");
        }

        // SM1 is the turn-approach indicator: every maneuver except a plain
        // "straight" approach shows a countdown in the 300 m zone. Left turns
        // are 902, everything else (right family, U-turn, roundabout,
        // finish) is 901 – matching the official MV Ride capture. The icon
        // keys come from NavigationIconMapper (single source of truth), no
        // substring matching.
        if (input.DistanceToTurnM is > 0 and <= 300
            && input.ManeuverIcon != NavigationIconMapper.IconStraight)
        {
            string sm1Type = input.ManeuverIcon is NavigationIconMapper.IconTurnLeft
                    or NavigationIconMapper.IconSlightLeft
                    or NavigationIconMapper.IconSharpLeft
                ? "902"
                : "901";
            int countdown = Math.Max(0, Math.Min(7, (int)(input.DistanceToTurnM / 40)));
            // On-change SM1 (same policy as NAVI): the countdown has only 8
            // values (40 m buckets), so writing it on every 1 Hz tick was
            // duplicate traffic. The key carries the maneuver index, so a new
            // maneuver (countdown resets to 7) and re-entry into the 300 m
            // zone always send – inside one maneuver the distance is
            // monotonic, so buckets never repeat and GPS jitter within the
            // same bucket is suppressed instead of re-sent.
            string sm1Key = $"{input.CurrentManeuverIndex}|{sm1Type}|{countdown}";
            if (sm1Key != _lastSm1Key)
            {
                try
                {
                    await SendSm1CountdownAsync(device, sm1Type, countdown);
                    // Marked only once delivered: a frame dropped by a full
                    // W2R buffer is retried on the next tick (writes no longer
                    // block, so there is no retry storm to guard against).
                    _lastSm1Key = sm1Key;
                }
                catch (Exception ex)
                {
                    // Non-critical frame. Log for field diagnostics.
                    Log.Debug(ex, "SM1 frame failed – retry next tick");
                }
            }
        }
        // Log the navigation update for debugging
        Log.Information("BLE-LOGGER: {Line}", $"NAV UPDATE: idx={input.CurrentManeuverIndex}, icon={input.ManeuverIcon}, dist={input.DistanceToTurnM:F0}m, speed={input.SpeedKmh:F0}km/h");
    }

    public async Task SendNavigationFinishAsync(IBleConnectedDevice device)
    {
        Log.Debug("MV Agusta: Navigation Finish");
        // Build and send FINISH frame – log it so we can trace the end of a route.
        byte[] frame = BuildFrame(Commands.FINISH, "", "", "");
        Log.Information("BLE-LOGGER: {Line}", $"SEND FINISH frame: {BitConverter.ToString(frame)}");
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: false);
        // Keepalive cadence is owned by the navigation coordinator, so there
        // is nothing to stop here – it observes the navigation-stop event.
    }

    public Task SendNavigationStopAsync(IBleConnectedDevice device)
    {
        // For MV Agusta, there is no separate STOP command – FINISH is used
        // for both destination reached and user-cancelled navigation
        // (confirmed via BLE trace analysis).
        Log.Debug("MV Agusta: Navigation Stop (user cancelled) – sending FINISH");
        return SendNavigationFinishAsync(device);
    }

    public async Task SendOffRouteAlertAsync(IBleConnectedDevice device)
    {
        // RENAVI format (from pklg capture): RENAVI|\x1e|\x1e|
        // All fields empty – the bike switches to rerouting mode based on
        // the command alone. Detection details are logged by the caller.
        byte[] frame = BuildFrame(Commands.RENAVI, "", "", "");
        Log.Information("BLE-LOGGER: {Line}", $"SEND RENAVI frame: {BitConverter.ToString(frame)}");
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: false);
    }

    /// <summary>
    /// Sends a PING keepalive frame (official MV Ride keepalive mechanism).
    /// PING format: \rPING\u001E\u001E\u001E\r (4 fields, all empty after command).
    /// This is the ONLY PING logic in the plugin: the 15 s cadence, the 5 s
    /// post-NAVI skip and start/stop with the navigation session are owned
    /// by the BleNavigationCoordinator, which calls this on the live link.
    /// </summary>
    public async Task SendKeepAliveAsync(IBleConnectedDevice device)
    {
        byte[] frame = BuildFrame(Commands.PING, "", "", "");
        Log.Information("BLE-LOGGER: {Line}", $"SEND PING frame: {BitConverter.ToString(frame)}");
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: false);
        Log.Debug("MV Agusta: Sent PING keepalive");
    }

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

    private async Task SendStatusFrameAsync(IBleConnectedDevice device, double remainingDistanceM, double distanceToTurnM)
    {
        // SM format: SM|speed_field|remainingDistanceM|distanceToTurnM
        // Field 1 is "0" in official captures.
        byte[] smFrame = BuildFrame(Commands.SM,
            "0",  
            remainingDistanceM.ToString("F0"),
            distanceToTurnM.ToString("F0"));
        Log.Information("BLE-LOGGER: {Line}", $"SEND SM frame: {BitConverter.ToString(smFrame)}");
        await device.WriteAsync(ControlWriteCharacteristicUuid, smFrame, withResponse: false);
    }

    // Frame layout: 0x0D + command + (0x1E + field)* + 0x0D
    private static byte[] BuildFrame(string command, params string[] fields)
    {
        char cr = '\r', rs = '\u001E';
        return Encoding.UTF8.GetBytes($"{cr}{command}{string.Concat(fields.Select(f => $"{rs}{f}"))}{cr}");
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

    // ─── Valhalla > MV Agusta Icon Mapping ─────────────────────────────
    // Moved here from NavigationService to keep protocol details in the plugin.

    /// <summary>
    /// Sends an SM1 countdown frame (turn approach indicator).
    /// MV Ride sends SM1|902|X for left turns, SM1|901|X for right turns.
    /// The countdown X goes from ~7 down to 0 as you approach the turn.
    /// </summary>
    private async Task SendSm1CountdownAsync(IBleConnectedDevice device, string sm1Type, int countdown)
    {
        byte[] frame = BuildFrame(Commands.SM1, sm1Type, countdown.ToString(), "");
        Log.Information("BLE-LOGGER: {Line}", $"SEND SM1 frame: {BitConverter.ToString(frame)}");
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: false);
    }
}
