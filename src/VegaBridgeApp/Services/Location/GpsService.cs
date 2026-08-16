using Serilog;
using Shiny.Locations;

namespace VegaBridgeApp.Services.Location;

/// <summary>
/// High-level GPS tracking service built on Shiny.Locations.
/// 
/// Provides foreground position events, breadcrumb recording, and
/// convenience access to current speed / heading / accuracy.
/// 
/// Usage:
///   inject IGpsManager for direct Shiny access, or inject GpsService
///   for the higher-level API (recommended for UI pages).
/// </summary>
public class GpsService : IDisposable
{
    private readonly IGpsManager _gpsManager;

    private readonly List<GpsReading> _breadcrumb = [];
    private readonly object _breadcrumbLock = new();
    private const int BreadcrumbMaxPoints = 1000;

    public GpsService(IGpsManager gpsManager)
    {
        _gpsManager = gpsManager;

        // Subscribe to foreground event from IGpsManager
        _gpsManager.GpsReadingReceived += OnForegroundReading;

        // Subscribe to background readings forwarded by GpsDelegate
        GpsDelegate.ReadingReceived += OnBackgroundReading;
    }

    // ── Events ───────────────────────────────────────────────────────────

    /// <summary>Fired on the UI thread whenever a new GPS reading arrives.</summary>
    public event Action<GpsReading>? ReadingReceived;

    /// <summary>Fired when tracking starts or stops.</summary>
    public event Action<bool>? TrackingChanged;

    // ── Properties ───────────────────────────────────────────────────────

    public bool IsTracking => _gpsManager.IsListening();

    public GpsReading? LastReading { get; private set; }

    /// <summary>
    /// Current speed in km/h, derived from the last reading.
    /// Returns 0 when no reading is available or speed is unknown.
    /// </summary>
    public double CurrentSpeedKmh
    {
        get
        {
            if (LastReading == null) return 0;
            double kmh = LastReading.Speed * 3.6;
            return double.IsNaN(kmh) || double.IsInfinity(kmh) || kmh < 0 ? 0 : kmh;
        }
    }

    /// <summary>Current position accuracy in meters, or 0 when unknown.</summary>
    public double CurrentAccuracy => LastReading?.PositionAccuracy ?? 0;

    /// <summary>Read-only snapshot of the breadcrumb trail.</summary>
    public IReadOnlyList<GpsReading> Breadcrumb
    {
        get
        {
            lock (_breadcrumbLock)
            {
                return _breadcrumb.ToList();
            }
        }
    }

    // ── Last known position ─────────────────────────────────────────────

    /// <summary>
    /// Returns the last cached reading, or tries to get a quick fix.<br/>
    /// Does NOT start the GPS listener – safe to call anytime.
    /// </summary>
    public async Task GetLastReadingOrCurrentAsync()
    {
        try
        {
            GpsReading? reading = await _gpsManager.GetLastReadingOrCurrentPosition();
            if (reading != null)
                LastReading = reading;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GetLastReadingOrCurrent failed");
        }
    }

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Request location permission using Shiny's GPS manager on iOS
    /// (avoids deprecated CoreLocation API). Falls back to MAUI Permissions.
    /// </summary>
    public async Task<bool> RequestPermissionAsync()
    {
        Log.Debug("Checking GPS permission…");
        
        try
        {
            #if DEBUG
                GpsRequest request = new() {AutoRestart = true, BackgroundMode = GpsBackgroundMode.None, RequestPreciseAccuracy = true};
            #else
                GpsRequest request = new() {AutoRestart = true, BackgroundMode = GpsBackgroundMode.Realtime, RequestPreciseAccuracy = true};
            #endif
            AccessState shinyAccess = _gpsManager.GetCurrentStatus(request);

            if (shinyAccess == AccessState.Available)
            {
                return true;
            }
            
            shinyAccess = await _gpsManager.RequestAccess(request);
            Log.Debug("Shiny GPS access: {Access}", shinyAccess);

            return shinyAccess switch
            {
                AccessState.Available => true,
                AccessState.Denied or AccessState.Restricted => false,
                _ => throw new ArgumentOutOfRangeException()
            };
            
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Shiny GPS access failed");
        }

        return  false;
    }

    /// <summary>
    /// Start continuous GPS tracking with the given configuration.
    /// </summary>
    /// <param name="backgroundMode">
    /// <c>true</c> for realtime background GPS (iOS: precise, Android: ~1s),
    /// <c>false</c> for foreground-only.
    /// </param>
    public async Task StartTrackingAsync(bool backgroundMode = true)
    {
        if (IsTracking)
        {
            Log.Debug("GPS tracking already active – ignoring start request");
            return;
        }

        bool granted = await RequestPermissionAsync();
        if (!granted)
        {
            throw new InvalidOperationException(
                "GPS permission not granted");
        }

        GpsRequest request = backgroundMode
            ? GpsRequest.Realtime(true)
            : GpsRequest.Foreground;

        await _gpsManager.StartListener(request);

        TrackingChanged?.Invoke(true);

        Log.Information("GPS tracking started (background: {Bg})", backgroundMode);
    }
    
    /// <summary>Stop GPS tracking.</summary>
    public async Task StopTrackingAsync()
    {
        if (!IsTracking) return;

        await _gpsManager.StopListener();
        TrackingChanged?.Invoke(false);

        Log.Information("GPS tracking stopped ({BreadcrumbCount} points collected)",
            _breadcrumb.Count);
    }

    /// <summary>Clear the breadcrumb trail.</summary>
    public void ClearBreadcrumb()
    {
        lock (_breadcrumbLock)
        {
            _breadcrumb.Clear();
        }

        Log.Debug("Breadcrumb cleared");
    }

    // ── Internal event handlers ──────────────────────────────────────────

    private void OnForegroundReading(object? sender, GpsReading reading)
    {
        LastReading = reading;
        lock (_breadcrumbLock)
        {
            _breadcrumb.Add(reading);
            if (_breadcrumb.Count > BreadcrumbMaxPoints)
                _breadcrumb.RemoveRange(0, _breadcrumb.Count - BreadcrumbMaxPoints);
        }
        ReadingReceived?.Invoke(reading);
    }

    private void OnBackgroundReading(GpsReading reading)
    {
        LastReading = reading;
        lock (_breadcrumbLock)
        {
            _breadcrumb.Add(reading);
            if (_breadcrumb.Count > BreadcrumbMaxPoints)
                _breadcrumb.RemoveRange(0, _breadcrumb.Count - BreadcrumbMaxPoints);
        }
        ReadingReceived?.Invoke(reading);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gpsManager.GpsReadingReceived -= OnForegroundReading;
        GpsDelegate.ReadingReceived -= OnBackgroundReading;
    }
}
