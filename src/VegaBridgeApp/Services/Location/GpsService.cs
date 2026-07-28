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
    private GpsReading? _lastReading;

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

    public bool IsTracking { get; private set; }

    public GpsReading? LastReading => _lastReading;

    /// <summary>
    /// Current speed in km/h, derived from the last reading.
    /// Returns 0 when no reading is available or speed is unknown.
    /// </summary>
    public double CurrentSpeedKmh
    {
        get
        {
            if (_lastReading == null) return 0;
            double kmh = _lastReading.Speed * 3.6;
            return double.IsNaN(kmh) || double.IsInfinity(kmh) || kmh < 0 ? 0 : kmh;
        }
    }

    /// <summary>Current heading in degrees (0–360), or 0 when unknown.</summary>
    public double CurrentHeading => _lastReading?.Heading ?? 0;

    /// <summary>Current position accuracy in meters, or 0 when unknown.</summary>
    public double CurrentAccuracy => _lastReading?.PositionAccuracy ?? 0;

    /// <summary>
    /// Altitude in meters above sea level, or 0 when unknown.
    /// </summary>
    public double CurrentAltitude => _lastReading?.Altitude ?? 0;

    /// <summary>Read-only snapshot of the breadcrumb trail.</summary>
    public IReadOnlyList<GpsReading> Breadcrumb => _breadcrumb.AsReadOnly();

    // ── Last known position ─────────────────────────────────────────────

    /// <summary>
    /// Returns the last cached reading, or tries to get a quick fix.<br/>
    /// Does NOT start the GPS listener – safe to call anytime.
    /// </summary>
    public async Task<GpsReading?> GetLastReadingOrCurrentAsync()
    {
        if (_lastReading != null)
            return _lastReading;

        try
        {
            GpsReading? reading = await _gpsManager.GetLastReadingOrCurrentPosition();
            if (reading != null)
                _lastReading = reading;
            return reading;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GetLastReadingOrCurrent failed");
            return null;
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

        // Shiny-first: nutzt moderne iOS-Authorization-API statt deprecated CLLocationManager
        try
        {
            AccessState shinyAccess = await Task.Run(() =>
                _gpsManager.RequestAccess(new GpsRequest(GpsBackgroundMode.None)));
            Log.Debug("Shiny GPS access: {Access}", shinyAccess);

            if (shinyAccess == AccessState.Available)
                return true;
            if (shinyAccess is AccessState.Denied or AccessState.Restricted)
                return false;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Shiny GPS access failed, falling back to MAUI Permissions");
        }

        // MAUI Permissions fallback
        try
        {
            PermissionStatus status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (status == PermissionStatus.Granted)
                return true;

            status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            return status == PermissionStatus.Granted;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GPS permission request failed");
            return false;
        }
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
                "GPS permission not granted. Call RequestPermissionAsync() first.");
        }

        GpsRequest request = backgroundMode
            ? GpsRequest.Realtime(true)
            : GpsRequest.Foreground;

        await _gpsManager.StartListener(request);
        IsTracking = true;
        TrackingChanged?.Invoke(true);

        Log.Information("GPS tracking started (background: {Bg})", backgroundMode);
    }

    /// <summary>Stop GPS tracking.</summary>
    public async Task StopTrackingAsync()
    {
        if (!IsTracking) return;

        await _gpsManager.StopListener();
        IsTracking = false;
        TrackingChanged?.Invoke(false);

        Log.Information("GPS tracking stopped ({BreadcrumbCount} points collected)",
            _breadcrumb.Count);
    }

    /// <summary>Clear the breadcrumb trail.</summary>
    public void ClearBreadcrumb()
    {
        _breadcrumb.Clear();
        Log.Debug("Breadcrumb cleared");
    }

    // ── Internal event handlers ──────────────────────────────────────────

    private void OnForegroundReading(object? sender, GpsReading reading)
    {
        _lastReading = reading;
        _breadcrumb.Add(reading);
        ReadingReceived?.Invoke(reading);
    }

    private void OnBackgroundReading(GpsReading reading)
    {
        _lastReading = reading;
        _breadcrumb.Add(reading);
        ReadingReceived?.Invoke(reading);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gpsManager.GpsReadingReceived -= OnForegroundReading;
        GpsDelegate.ReadingReceived -= OnBackgroundReading;
    }
}
