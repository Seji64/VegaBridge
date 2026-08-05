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

    // Heartbeat fields
    private PeriodicTimer? _heartbeatTimer;
    private CancellationTokenSource? _heartbeatCts;
    private bool _isDisposed;

    public bool IsCompatible(BleDeviceInfo device)
    {
        // MV Agusta devices typically have "MV" or "BRUTALE" in their name.
        return device.Name.Contains("MV", StringComparison.OrdinalIgnoreCase) || 
               device.Name.Contains("BRUTALE", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SendAsync(IBleConnectedDevice device, string command, params string[] fields)
    {
        byte[] frame = BuildFrame(command, fields);
        // Use Write-without-Response for this characteristic as the device does not support response writes
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: false);
    }

    public async Task SendTestAsync(IBleConnectedDevice device)
    {
        // Test frame: sends FINISH (destination reached) to verify BLE connectivity.
        byte[] frame = BuildFrame(Commands.FINISH, "", "", "");
        // Use Write-without-Response for this characteristic as the device does not support response writes
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: false);
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
        
        // Start heartbeat when navigation begins
        await StartHeartbeatAsync(device);
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
        await device.WriteAsync(ControlWriteCharacteristicUuid, naviFrame, withResponse: false);

        // Send SM (Status/Motion) frame with current metrics
        await SendStatusFrameAsync(device, input.SpeedKmh, input.RemainingDistanceKm * 1000, input.DistanceToTurnM);
    }

    public async Task SendNavigationFinishAsync(IBleConnectedDevice device)
    {
        Log.Debug("MV Agusta: Navigation Finish");
        byte[] frame = BuildFrame(Commands.FINISH, "", "", "");
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: false);
        
        // Stop heartbeat when navigation ends
        StopHeartbeat();
    }

    public async Task SendOffRouteAlertAsync(IBleConnectedDevice device, OffRouteAlertInput input)
    {
        Log.Warning("MV Agusta: Off-Route Alert - {Dist:F1}m at {Lat},{Lon}", input.DistanceMeters, input.Latitude, input.Longitude);
        
        // Signal the motorcycle that the route is being recalculated
        byte[] frame = BuildFrame(Commands.RENAVI, "off-route", "OFF ROUTE", $"{input.DistanceMeters:F0}m");
        await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: false);
    }

    // ─── Heartbeat Implementation ───────────────────────────────────────

    /// <summary>
    /// Starts the GUI1 keep-alive heartbeat timer (sends every 2-3 seconds).
    /// </summary>
    private async Task StartHeartbeatAsync(IBleConnectedDevice device)
    {
        // Stop any existing timer
        StopHeartbeat();
        
        _heartbeatCts = new CancellationTokenSource();
        _heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(2.5)); // Send every 2.5 seconds
        
        // Start the heartbeat loop
        await Task.Run(async () =>
        {
            try
            {
                while (await _heartbeatTimer.WaitForNextTickAsync(_heartbeatCts.Token))
                {
                    if (_isDisposed) break;
                    await SendGui1KeepAliveAsync(device);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
            }
            catch (Exception ex)
            {
                Log.Error(ex, "MV Agusta: Heartbeat error");
            }
        }, _heartbeatCts.Token);
    }

    /// <summary>
    /// Stops the heartbeat timer.
    /// </summary>
    private void StopHeartbeat()
    {
        if (_heartbeatCts != null)
        {
            _heartbeatCts.Cancel();
            _heartbeatCts.Dispose();
            _heartbeatCts = null;
        }
        
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    /// <summary>
    /// Creates and sends a GUI1 keep-alive frame with a session ID.
    /// </summary>
    private async Task SendGui1KeepAliveAsync(IBleConnectedDevice device)
    {
        try
        {
            // Generate a fresh cryptographically random session ID for each heartbeat tick.
            // This satisfies the spec requirement that the ID changes every 1-3 seconds and avoids magic strings.
            string sessionId = GenerateSessionId();
            
            byte[] frame = BuildGui1Frame(sessionId);
            // GUI1 uses Write-with-Response on handle 0x002A (same UUID as control characteristic)
            await device.WriteAsync(ControlWriteCharacteristicUuid, frame, withResponse: true);
            Log.Debug("MV Agusta: Sent GUI1 keep-alive {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MV Agusta: Failed to send GUI1 keep-alive");
        }
    }

    /// <summary>
    /// Builds a GUI1 frame: \rGUI1\u001E<session_id>\r
    /// </summary>
    private byte[] BuildGui1Frame(string sessionId)
    {
        using MemoryStream ms = new();
        ms.WriteByte(Cr);
        ms.Write(Encoding.UTF8.GetBytes(Commands.GUI1));
        ms.WriteByte(Rs);
        ms.Write(Encoding.UTF8.GetBytes(sessionId));
        ms.WriteByte(Cr);
        return ms.ToArray();
    }

    /// <summary>
    /// Generates a cryptographically random 8-byte session ID (16 hex characters).
    /// This satisfies the protocol requirement that GUI1 session IDs change each heartbeat.
    /// </summary>
    private string GenerateSessionId()
    {
        // Use RandomNumberGenerator for secure randomness
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        byte[] randomBytes = new byte[8]; // 8 bytes = 16 hex characters
        rng.GetBytes(randomBytes);
        // Convert to uppercase hex string (matching the spec's hex format)
        return Convert.ToHexString(randomBytes).ToUpperInvariant();
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
        await device.WriteAsync(ControlWriteCharacteristicUuid, smFrame, withResponse: false);
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

    // ─── IAsyncDisposable Implementation ───────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        
        StopHeartbeat();
        
        GC.SuppressFinalize(this);
    }
}
