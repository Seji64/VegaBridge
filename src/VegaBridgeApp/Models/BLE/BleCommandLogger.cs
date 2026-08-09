using System;
using System.Collections.Generic;

namespace VegaBridgeApp.Models.BLE;

/// <summary>
/// Simple in‑memory logger for BLE frames. The UI can retrieve the logged lines and allow the user to
/// download them (e.g., via a temporary "Export Log" button). This avoids pulling the whole Serilog
/// store into the UI and gives deterministic ordering of sent/received frames.
/// </summary>
public static class BleCommandLogger
{
    private static readonly List<string> LogLines = new();

    /// <summary>Gets a read‑only snapshot of the current log.</summary>
    public static IReadOnlyList<string> GetLog() => LogLines.AsReadOnly();

    /// <summary>Clears the current log.</summary>
    public static void ClearLog() => LogLines.Clear();

    /// <summary>Appends a line with a UTC timestamp. Also forwards to Serilog for live visibility.</summary>
    public static void Log(string line)
    {
        LogLines.Add($"{DateTime.UtcNow:O} {line}");
        Serilog.Log.Information("BLE‑LOGGER: {Line}", line);
    }
}
