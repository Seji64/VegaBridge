using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace VegaBridgeApp.Services.Debug;

/// <summary>
/// In-memory Serilog sink that appends every log line to a StringBuilder so
/// it can be exported from the UI. Console output is not reliably visible in
/// MAUI (device/simulator), so this is the primary way to inspect logs while
/// testing. Capped – the oldest lines are dropped first on long rides.
/// Collecting only runs while <see cref="IsEnabled"/> is true, so the app
/// does not pay for buffering during normal use.
/// </summary>
public sealed class DebugLogSink : ILogEventSink
{
    /// <summary>Shared instance; wired into Serilog and DI at startup.</summary>
    public static DebugLogSink Instance { get; } = new();

    private readonly StringBuilder _sb = new();
    private readonly object _lock = new();
    private const int MaxChars = 200_000;

    /// <summary>
    /// When false, Emit discards events immediately (no buffering cost).
    /// Persisted in Preferences under "debug_logging_enabled".
    /// </summary>
    public bool IsEnabled { get; private set; } =
        Preferences.Default.Get("debug_logging_enabled", false);

    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            IsEnabled = enabled;
            if (!enabled)
                _sb.Clear();
        }
        Preferences.Default.Set("debug_logging_enabled", enabled);
    }

    public void Emit(LogEvent logEvent)
    {
        if (!IsEnabled) return;

        string line = $"{logEvent.Timestamp:HH:mm:ss.fff} {logEvent.Level}: {logEvent.RenderMessage()}";
        lock (_lock)
        {
            _sb.AppendLine(line);
            if (_sb.Length > MaxChars)
                _sb.Remove(0, _sb.Length - MaxChars);
        }
    }

    public string GetLog()
    {
        lock (_lock) return _sb.ToString();
    }

    public void Clear()
    {
        lock (_lock) _sb.Clear();
    }
}
