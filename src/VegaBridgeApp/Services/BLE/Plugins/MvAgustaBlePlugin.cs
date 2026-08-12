using System.Text;
using Serilog;
using VegaBridgeApp.Models.BLE;
using VegaBridgeApp.Models.BLE.MvAgusta;

// ReSharper disable InvalidXmlDocComment

namespace VegaBridgeApp.Services.BLE.Plugins;

/// <summary>
/// MV Agusta BLE plugin – implements the protocol for MV Agusta motorcycles.
/// </summary>
public class MvAgustaBlePlugin : IBleDevicePlugin, IAsyncDisposable
{
    private const byte Cr = 0x0D;
    private const byte Rs = 0x1E;

    public string ManufacturerId => "MVAGUSTA";
    public string DisplayName => "MV Agusta";
    public string BrandName => "MV AGUSTA";
    public Guid ServiceUuid => Guid.Parse("00003719-0000-1000-8000-00805f9b34fb");
    public string ControlWriteCharacteristicUuid => "00002345-0000-1000-8000-00805f9b34fb";
    public string ReadCharacteristicUuid => "00001234-0000-1000-8000-00805f9b34fb";

    // Heartbeat fields - PING keepalive (official MV Ride uses PING, not GUI1 writes)
    private PeriodicTimer? _pingTimer;
    private CancellationTokenSource? _pingCts;
    private Task? _pingTask;
    private string? _lastBikeSessionId;
    private IBleConnectedDevice? _connectedDevice; // Store for GUI1 responses
    private bool _isDisposed;

    /// <summary>
    /// Global switch for the GUI1-echo response.
    /// DEFAULT: OFF – the official MV Ride capture (mvride_nav.txt) shows 0 GUI1 writes
    /// from the phone; PING keepalive + NAVI/SM traffic keep the session alive.
    /// Only enable for A/B testing when investigating session drops on the bike.
    /// </summary>
    public static bool Gui1ResponseEnabled { get; set; }

    /// <summary>
    /// Gets the last GUI1 session ID received from the bike (for reference only).
    /// </summary>
    public string? LastBikeSessionId => _lastBikeSessionId;

    public bool IsCompatible(BleDeviceInfo device)
    {
        // MV Agusta devices typically have "MV" or "BRUTALE" in their name.
        return device.Name.Contains("MV", StringComparison.OrdinalIgnoreCase) || 
               device.Name.Contains("BRUTALE", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SendAsync(IBleConnectedDevice device, string command, params string[] fields)
    {
        byte[] frame = BuildFrame(command, fields);
        BleCommandLogger.Log($"SEND {command} frame: {BitConverter.ToString(frame)}");
        // Remember device so GUI1 responses to bike notifications can be sent
        _connectedDevice = device;
        // Use Write-without-Response for this characteristic as the device does not support response writes
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: false);
    }

    public async Task SendTestAsync(IBleConnectedDevice device)
    {
        // Test frame: sends a WhatsApp‑style MSG command so the user sees a readable message on the bike.
        // The MSG format is: ⏎MSG⏝<appId>⏝<message>⏝<title>⏎
        // Using "whatsapp" as the appId mirrors the real MV Ride app behaviour and guarantees a visible payload.
        byte[] frame = BuildFrame(Commands.MSG, "whatsapp", "Test from VegaBridge", "VegaBridge");
        // Log the raw frame for debugging
        BleCommandLogger.Log($"SEND MSG frame: {BitConverter.ToString(frame)}");
        // Write‑without‑Response is fine for MSG – the bike only needs to display the payload.
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: false);
    }

    // ─── Semantic Navigation Implementation ──────────────────────────────

    public async Task SendNavigationStartAsync(IBleConnectedDevice device, NavigationStartInput input)
    {
        Log.Debug("MV Agusta: Navigation Start - {Distance:F1}km, {Time:F0}min", input.TotalDistanceKm, input.TotalTimeMin);
        BleCommandLogger.Log($"NAV START: distance={input.TotalDistanceKm:F1}km, time={input.TotalTimeMin:F0}min, maneuvers={input.UpcomingManeuvers?.Count ?? 0}");
        
        // Store device for GUI1 responses (bike sends GUI1 notifications, we must respond)
        _connectedDevice = device;
        
        // DEST format (from pklg capture): DEST|\x1e|lon\x1e|lat\x1e|
        // Field 1 (address) is empty in the official MV Ride app.
        // Field 2 = longitude, field 3 = latitude (both 6 decimal places).
        // TODO: Pass real start coordinates through NavigationStartInput once available.
        string lon = "9.258020";  // placeholder
        string lat = "48.775730"; // placeholder
        await SendAsync(device, Commands.DEST, "", lon, lat);
        await Task.Delay(200);
        
        // REM format (from pklg capture): REM|\x1e|<meters>\x1e|
        // 3 RS separators → 4 fields: command, empty, meters, empty
        await SendAsync(device, Commands.REM, "", (input.TotalDistanceKm * 1000).ToString("F0"), "");
        await Task.Delay(200);
        
        // Start PING keepalive when navigation begins
        await StartPingAsync(device);
    }

    public async Task SendNavigationUpdateAsync(IBleConnectedDevice device, NavigationUpdateInput input)
    {
        Log.Debug("MV Agusta: Navigation Update - Maneuver {Index}/{Total}: {Icon}, Dist: {Dist:F0}m, Speed: {Speed:F0}km/h", 
            input.CurrentManeuverIndex + 1, input.TotalManeuvers, input.ManeuverIcon, input.DistanceToTurnM, input.SpeedKmh);

        // NAVI format: NAVI|icon|navigationGuide|intersectionName
        // Per BluetoothService.java (mvride v1.4.3):
        // - navigationGuide = direction.getDescription() (e.g., "Links abbiegen\nRosenstraße")
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
        
        byte[] naviFrame = BuildFrame(Commands.NAVI,
            input.ManeuverIcon,
            navigationGuide,
            intersectionName);
        BleCommandLogger.Log($"SEND NAVI frame: {BitConverter.ToString(naviFrame)}");
        await device.WriteAsync(ControlWriteCharacteristicUuid, naviFrame, withResponse: false);

        // Send SM (Status/Motion) frame with current metrics
        await SendStatusFrameAsync(device, input.RemainingDistanceKm * 1000, input.DistanceToTurnM);
        
        // Send SM1 countdown frame when approaching a turn (within ~300m for left, ~250m for right)
        // MV Ride sends SM1 with type 902 (left turns) or 901 (right turns) and a countdown value.
        // The countdown decreases from ~7 to 0 as you approach the turn.
        if (input.DistanceToTurnM is <= 300 and > 0)
        {
            // Determine SM1 type based on maneuver icon
            string sm1Type = input.ManeuverIcon.Contains("left", StringComparison.OrdinalIgnoreCase) ? "902" : "901";
            // Countdown: 7 at ~300m, decreasing to 0 at the turn
            int countdown = Math.Max(0, Math.Min(7, (int)(input.DistanceToTurnM / 40)));
            await SendSm1CountdownAsync(device, sm1Type, countdown);
        }
        
        // Add a slight delay to give the bike time to process the user‑visible frames
        await Task.Delay(250); // 250 ms – experimentally safe for most MV phones
        
        // Log the navigation update for debugging
        BleCommandLogger.Log($"NAV UPDATE: idx={input.CurrentManeuverIndex}, icon={input.ManeuverIcon}, dist={input.DistanceToTurnM:F0}m, speed={input.SpeedKmh:F0}km/h");
    }

    public async Task SendNavigationFinishAsync(IBleConnectedDevice device)
    {
        Log.Debug("MV Agusta: Navigation Finish");
        // Build and send FINISH frame – log it so we can trace the end of a route.
        byte[] frame = BuildFrame(Commands.FINISH, "", "", "");
        BleCommandLogger.Log($"SEND FINISH frame: {BitConverter.ToString(frame)}");
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: false);
        
        // Stop keepalive when navigation ends
        await StopPingAsync();
    }

    public async Task SendNavigationStopAsync(IBleConnectedDevice device)
    {
        Log.Debug("MV Agusta: Navigation Stop (user cancelled)");
        // For MV Agusta, there is no separate STOP command - FINISH is used for both
        // destination reached and user-cancelled navigation (confirmed via BLE trace analysis)
        byte[] frame = BuildFrame(Commands.FINISH, "", "", "");
        BleCommandLogger.Log($"SEND FINISH (STOP) frame: {BitConverter.ToString(frame)}");
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: false);
        
        // Stop keepalive when navigation ends
        await StopPingAsync();
    }

    public async Task SendOffRouteAlertAsync(IBleConnectedDevice device, OffRouteAlertInput input)
    {
        Log.Warning("MV Agusta: Off-Route Alert - {Dist:F1}m at {Lat},{Lon}", input.DistanceMeters, input.Latitude, input.Longitude);
        
        // RENAVI format (from pklg capture): RENAVI|\x1e|\x1e|
        // All fields empty – the bike switches to rerouting mode based on the command alone.
        byte[] frame = BuildFrame(Commands.RENAVI, "", "", "");
        BleCommandLogger.Log($"SEND RENAVI frame: {BitConverter.ToString(frame)}");
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: false);
        
        BleCommandLogger.Log($"OFF-ROUTE ALERT: dist={input.DistanceMeters:F0}m, lat={input.Latitude:F6}, lon={input.Longitude:F6}");
    }

    /// <summary>
    /// Starts the PING keepalive timer (sends every ~15 seconds, matching official app behavior).
    /// </summary>
    private async Task StartPingAsync(IBleConnectedDevice device)
    {
        // Stop any existing timer
        await StopPingAsync();
        
        _pingCts = new CancellationTokenSource();
        _pingTimer = new PeriodicTimer(TimeSpan.FromSeconds(15)); // Official app sends PING once in capture, but keepalive every ~15s
        
        // Start the ping loop and store the task for proper disposal
        _pingTask = Task.Run(async () =>
        {
            try
            {
                while (await _pingTimer.WaitForNextTickAsync(_pingCts.Token))
                {
                    if (_isDisposed) break;
                    await SendPingAsync(device);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
            }
            catch (Exception ex) when (!_isDisposed)
            {
                Log.Error(ex, "MV Agusta: PING keepalive error");
            }
        }, _pingCts.Token);
    }

    /// <summary>
    /// Stops the PING keepalive timer and waits for the task to complete.
    /// </summary>
    private async Task StopPingAsync()
    {
        if (_pingCts != null)
        {
            _pingCts.Cancel();
            _pingCts.Dispose();
            _pingCts = null;
        }
        
        _pingTimer?.Dispose();
        _pingTimer = null;

        // Wait for the ping task to finish gracefully
        if (_pingTask != null)
        {
            try
            {
                await _pingTask;
            }
            catch (OperationCanceledException) { /* Expected */ }
            catch (Exception ex)
            {
                Log.Warning(ex, "MV Agusta: PING task ended with error during stop");
            }
            _pingTask = null;
        }
    }

    /// <summary>
    /// Sends a PING keepalive frame (official MV Ride keepalive mechanism).
    /// PING format: \rPING\u001E\u001E\u001E\r (4 fields, all empty after command)
    /// </summary>
    private async Task SendPingAsync(IBleConnectedDevice device)
    {
        try
        {
            byte[] frame = BuildFrame(Commands.PING, "", "", "");
            BleCommandLogger.Log($"SEND PING frame: {BitConverter.ToString(frame)}");
            await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: false);
            Log.Debug("MV Agusta: Sent PING keepalive");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MV Agusta: Failed to send PING keepalive");
        }
    }

    /// <summary>
    /// Sends a GUI1 response frame (Write with Response) to acknowledge the bike's GUI1 notification.
    /// This is CRITICAL for keeping the navigation session alive.
    /// The bike sends GUI1 notifications on handle 0x002A with a session ID.
    /// We must respond with a GUI1 Write on the SAME handle (0x002A) using withResponse: true.
    /// </summary>
    private async Task SendGui1ResponseAsync(string bikeSessionId)
    {
        try
        {
            // GUI1 response format: \rGUI1\x1e<session_id>\x1e\x1e\r
            // Use the bike's session ID (echo pattern - confirmed working in 08.08 test)
            byte[] frame = BuildFrame(Commands.GUI1, bikeSessionId, "", "");
            BleCommandLogger.Log($"SEND GUI1 RESPONSE frame: {BitConverter.ToString(frame)}");
            
            // IMPORTANT: Write WITH Response on the GUI1 characteristic (0x002A)
            // Use the stored connected device
            if (_connectedDevice != null)
            {
                await _connectedDevice.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: true);
            }
            
            Log.Debug("MV Agusta: Sent GUI1 response for session {SessionId}", bikeSessionId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MV Agusta: Failed to send GUI1 response");
        }
    }

    // ─── Incoming Data Handling ──────────────────────────────────────────

    public void OnDataReceived(byte[] data)
    {
        if (TryParseFrame(data, out string command, out string[] fields))
        {
            // Handle GUI1 heartbeat from bike - update session ID AND respond with GUI1 write (with response)
            if (command == "GUI1" && fields.Length > 0)
            {
                _lastBikeSessionId = fields[0];
                BleCommandLogger.Log($"RECV GUI1 frame: {BitConverter.ToString(data)}, sessionId={fields[0]}");

                // GUI1 response is OPTIONAL (A/B test switch). The official MV Ride capture
                // shows the phone NEVER writes GUI1 – PING + NAVI/SM frames suffice.
                if (Gui1ResponseEnabled)
                {
                    _ = Task.Run(() => SendGui1ResponseAsync(fields[0]));
                }
            }
            // Logic to handle the parsed frame
            // In a real scenario, this might trigger an event or update a state machine.
            BleCommandLogger.Log($"RECV {command} frame: {BitConverter.ToString(data)}");
            Log.Debug("MV Agusta Frame Received: {Command}, Fields: {Fields}", command, string.Join(", ", fields));
        }
        else
        {
            BleCommandLogger.Log($"RECV INVALID frame: {BitConverter.ToString(data)}");
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
        BleCommandLogger.Log($"SEND SM frame: {BitConverter.ToString(smFrame)}");
        await device.WriteAsync(ControlWriteCharacteristicUuid, smFrame, withResponse: false);
        
        await Task.Delay(150);
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

    /// <summary>
    /// Sends an SM1 countdown frame (turn approach indicator).
    /// MV Ride sends SM1|902|X for left turns, SM1|901|X for right turns.
    /// The countdown X goes from ~7 down to 0 as you approach the turn.
    /// </summary>
    private async Task SendSm1CountdownAsync(IBleConnectedDevice device, string sm1Type, int countdown)
    {
        byte[] frame = BuildFrame(Commands.SM1, sm1Type, countdown.ToString(), "");
        BleCommandLogger.Log($"SEND SM1 frame: {BitConverter.ToString(frame)}");
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: false);
        await Task.Delay(100);
    }

    // ─── IAsyncDisposable Implementation ───────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        
        await StopPingAsync();
        
        GC.SuppressFinalize(this);
    }
}
