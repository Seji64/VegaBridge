using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace VegaBridgeApp.Services.Debug;

/// <summary>
/// In-memory Serilog sink backed by a fixed-size ring buffer.
/// Oldest lines are dropped when the buffer is full (O(1), no copying).
/// Collecting only runs while <see cref="IsEnabled"/> is true, so the app
/// does not pay for buffering during normal use.
/// Thread-safe: all public methods are guarded by a single lock.
/// </summary>
public sealed class DebugLogSink(int maxLines = 60_000) : ILogEventSink // ~100 min at 10 lines/sec
{
    /// <summary>Shared instance; wired into Serilog and DI at startup.</summary>
    public static DebugLogSink Instance { get; } = new();

    private readonly string[] _buffer = new string[maxLines];
    private int _head;   // next write position
    private int _count;  // current number of lines in buffer
    private long _totalLinesWritten;
    private readonly object _lock = new();

    /// <summary>
    /// When false, Emit discards events immediately (no buffering cost).
    /// Persisted in Preferences under "debug_logging_enabled".
    /// </summary>
    public bool IsEnabled { get; private set; } =
        Preferences.Default.Get("debug_logging_enabled", false);

    /// <summary>Number of lines currently in the buffer.</summary>
    public int LineCount { get { lock (_lock) return _count; } }

    /// <summary>Total lines written since last Clear (including dropped).</summary>
    public long TotalLinesWritten { get { lock (_lock) return _totalLinesWritten; } }

    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            IsEnabled = enabled;
            if (!enabled) ClearBuffer();
        }
        Preferences.Default.Set("debug_logging_enabled", enabled);
    }

    public void Emit(LogEvent logEvent)
    {
        if (!IsEnabled) return;

        string line;
        try
        {
            line = $"{logEvent.Timestamp:HH:mm:ss.fff} {logEvent.Level}: {logEvent.RenderMessage()}";
        }
        catch (Exception)
        {
            return; // RenderMessage can throw on malformed properties
        }

        lock (_lock)
        {
            _buffer[_head] = line;
            _head = (_head + 1) % maxLines;
            if (_count < maxLines) _count++;
            _totalLinesWritten++;
        }
    }

    /// <summary>
    /// Returns the buffered log lines as a single string (oldest first).
    /// Allocates a string proportional to buffer size — call only for export.
    /// </summary>
    public string GetLog()
    {
        lock (_lock)
        {
            if (_count == 0) return string.Empty;

            // Estimate ~120 chars/line to reduce reallocations
            StringBuilder sb = new StringBuilder(_count * 120);

            // Start from the oldest line
            int start = _count < maxLines ? 0 : _head;
            for (int i = 0; i < _count; i++)
            {
                int idx = (start + i) % maxLines;
                sb.AppendLine(_buffer[idx]);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Returns buffered lines as a list (avoids one giant string allocation).
    /// Useful for streaming export or chunked sharing.
    /// </summary>
    public List<string> GetLines()
    {
        lock (_lock)
        {
            if (_count == 0) return [];

            List<string> result = new List<string>(_count);
            int start = _count < maxLines ? 0 : _head;
            for (int i = 0; i < _count; i++)
            {
                int idx = (start + i) % maxLines;
                result.Add(_buffer[idx]);
            }
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock) ClearBuffer();
    }

    private void ClearBuffer()
    {
        // Clear references to allow GC of strings
        Array.Clear(_buffer, 0, maxLines);
        _head = 0;
        _count = 0;
        _totalLinesWritten = 0;
    }
}
