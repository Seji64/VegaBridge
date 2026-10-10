using Serilog;
using Shiny.Locations;
using VegaBridgeApp.Models.Navigation;
using VegaBridgeApp.Models.Valhalla;
using VegaBridgeApp.Services.Debug;
using VegaBridgeApp.Services.Navigation;
using VegaBridgeApp.Utils;
using Coordinate = VegaBridgeApp.Models.Valhalla.Coordinate;

namespace VegaBridgeApp.Services.Location;

/// <summary>
/// Offline ride test. Once armed, the NEXT navigation started on the map is
/// fed with GPS fixes interpolated along its route instead of the real GPS.
/// Everything downstream is the live-ride code path: NavigationService, map,
/// BleNavigationCoordinator, plugin and BLE writes, with the 1 Hz cadence,
/// fixes arriving off the UI thread (like Shiny's GPS queue) and display-off
/// operation (the real GPS listener keeps the app alive in the background).
/// Speed follows Valhalla's per-maneuver average, slows down before turns and
/// every 3rd turn waits 30 s like at a traffic light. One-shot, in-memory and
/// expiring after 10 min, so a later real ride is never simulated by accident.
/// </summary>
public sealed class GpsSimulator : INavigationSink
{
    private readonly NavigationService _navigation;
    private readonly GpsService _gps;
    private static readonly TimeSpan ArmedFor = TimeSpan.FromMinutes(10);
    private CancellationTokenSource? _cts;
    private DateTime _armedAt;
    private DateTime _startedAt;
    private double _drivenM;

    public GpsSimulator(NavigationService navigation, GpsService gps)
    {
        _navigation = navigation;
        _gps = gps;
        navigation.AddSink(this);
    }

    private bool _armed;
    public bool IsArmed => _armed && DateTime.UtcNow - _armedAt < ArmedFor;
    public bool IsRunning => _cts != null;
    public string LastResult { get; private set; } = string.Empty;
    public event Action? StateChanged;

    public void Arm()
    {
        _armed = true;
        _armedAt = DateTime.UtcNow;
        // A test without a capturable log is useless – the run owns its capture.
        DebugLogSink.Instance.SetEnabled(true);
        DebugLogSink.Instance.Clear();
        Log.Information("BLE-LOGGER: {Line}", "GPS-SIM ARMED: the next navigation start is driven by simulated GPS");
        StateChanged?.Invoke();
    }

    public void Disarm()
    {
        _armed = false;
        StateChanged?.Invoke();
    }

    // ─── INavigationSink ─────────────────────────────────────────────────

    public Task OnStartAsync(NavigationStartInfo start)
    {
        // A restart while running (new route) keeps the run going – the loop
        // re-snaps to the new route.
        if (!IsArmed || _cts != null)
            return Task.CompletedTask;

        _armed = false;
        _gps.IsSimulating = true;
        _startedAt = DateTime.UtcNow;
        _drivenM = 0;
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        // Thread pool: live fixes do not arrive on the UI thread either.
        _ = Task.Run(() => RunAsync(ct), ct);
        StateChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task OnFinishAsync()
    {
        Stop("Ziel erreicht");
        return Task.CompletedTask;
    }

    public Task OnCancelAsync()
    {
        Stop("Navigation beendet");
        return Task.CompletedTask;
    }

    public Task OnManeuverAsync(NavigationManeuverInfo maneuver) => Task.CompletedTask;
    public Task OnStatusAsync(NavigationStatus status) => Task.CompletedTask;
    public Task OnOffRouteAsync(double latitude, double longitude, double distanceMeters) => Task.CompletedTask;
    public Task OnRouteUpdatedAsync(RouteResponse response) => Task.CompletedTask;

    // ─── Ride loop ───────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        Log.Information("BLE-LOGGER: {Line}", "GPS-SIM START: driving the active route with simulated GPS at 1 Hz");

        IReadOnlyList<Coordinate>? route = null;
        double[] cum = [];
        double along = 0;          // metres along the current route
        int seg = 0;               // segment [seg, seg+1] containing `along`
        int passedTurns = 0;       // maneuver begins passed on this route
        int standTicks = 0;
        double heading = 0;

        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct))
            {
                IReadOnlyList<Coordinate> coords = _navigation.RouteCoordinates;
                IReadOnlyList<Maneuver> maneuvers = _navigation.Maneuvers;
                if (coords.Count < 2)
                    continue;

                if (!ReferenceEquals(coords, route))
                {
                    // Start or reroute: continue from the route point nearest
                    // to where the simulated rider is now.
                    Coordinate here = route == null ? coords[0] : PositionAt(route, cum, seg, along);
                    route = coords;
                    cum = Cumulative(coords);
                    int nearest = 0;
                    for (int i = 1; i < coords.Count; i++)
                    {
                        if (Distance(coords[i], here) < Distance(coords[nearest], here))
                            nearest = i;
                    }
                    seg = Math.Min(nearest, coords.Count - 2);
                    along = cum[nearest];
                    passedTurns = maneuvers.Count(m => m.BeginShapeIndex <= nearest);
                }

                double speed = 0;
                if (standTicks > 0)
                {
                    standTicks--;
                }
                else if (along < cum[^1])
                {
                    speed = SpeedAt(maneuvers, seg, along, cum);
                    double next = Math.Min(along + speed, cum[^1]);

                    // Stop at every 3rd turn for 30 s, like at a traffic light.
                    // Loop: one tick can pass several short maneuvers.
                    while (passedTurns < maneuvers.Count
                           && maneuvers[passedTurns].BeginShapeIndex < cum.Length
                           && next >= cum[maneuvers[passedTurns].BeginShapeIndex])
                    {
                        double turnAt = cum[maneuvers[passedTurns].BeginShapeIndex];
                        passedTurns++;
                        if (passedTurns % 3 == 0 && turnAt >= along)
                        {
                            next = turnAt;
                            standTicks = 30;
                            break;
                        }
                    }

                    _drivenM += next - along;
                    along = next;
                    while (seg < route.Count - 2 && cum[seg + 1] <= along)
                        seg++;
                    heading = GeoMath.BearingDeg(
                        route[seg].Latitude, route[seg].Longitude,
                        route[seg + 1].Latitude, route[seg + 1].Longitude);
                }
                // At the end the position stays put until NavigationService
                // detects the arrival (→ OnFinishAsync → Stop).

                Coordinate p = PositionAt(route, cum, seg, along);
                _gps.PublishSimulated(new GpsReading(
                    new Position(p.Latitude, p.Longitude),
                    PositionAccuracy: 5,
                    Timestamp: DateTimeOffset.UtcNow,
                    Heading: heading,
                    HeadingAccuracy: 10,
                    Altitude: 0,
                    Speed: speed,
                    SpeedAccuracy: 1,
                    IsStationary: speed == 0));
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() was called
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GPS-SIM failed");
            Stop("Fehler: " + ex.Message);
        }
    }

    private void Stop(string reason)
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
        if (cts == null)
            return;

        cts.Cancel();
        _gps.IsSimulating = false;
        TimeSpan elapsed = DateTime.UtcNow - _startedAt;
        LastResult = $"Simulation beendet ({reason}) nach {elapsed:hh\\:mm\\:ss}, {_drivenM / 1000:F1} km";
        Log.Information("BLE-LOGGER: {Line}", $"GPS-SIM END: {LastResult}");
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Valhalla's average speed of the maneuver the rider is on (city ≈ 30–50,
    /// B10 ≈ 80 km/h), at most ~22 km/h within 40 m before the next turn.
    /// </summary>
    private static double SpeedAt(IReadOnlyList<Maneuver> maneuvers, int seg, double along, double[] cum)
    {
        double speed = 13.9; // 50 km/h when Valhalla gives no time
        Maneuver? current = maneuvers.LastOrDefault(m => m.BeginShapeIndex <= seg);
        if (current is { Time: > 0, Length: > 0 })
            speed = Math.Clamp(current.Length * 1000 / current.Time, 4, 30);

        Maneuver? next = maneuvers.FirstOrDefault(m => m.BeginShapeIndex > seg && m.BeginShapeIndex < cum.Length);
        if (next != null && cum[next.BeginShapeIndex] - along < 40)
            speed = Math.Min(speed, 6);
        return speed;
    }

    private static Coordinate PositionAt(IReadOnlyList<Coordinate> route, double[] cum, int seg, double along)
    {
        double length = cum[seg + 1] - cum[seg];
        double t = length > 0 ? Math.Clamp((along - cum[seg]) / length, 0, 1) : 0;
        return new Coordinate(
            route[seg].Latitude + t * (route[seg + 1].Latitude - route[seg].Latitude),
            route[seg].Longitude + t * (route[seg + 1].Longitude - route[seg].Longitude),
            null);
    }

    private static double[] Cumulative(IReadOnlyList<Coordinate> coords)
    {
        double[] cum = new double[coords.Count];
        for (int i = 1; i < coords.Count; i++)
            cum[i] = cum[i - 1] + Distance(coords[i - 1], coords[i]);
        return cum;
    }

    private static double Distance(Coordinate a, Coordinate b)
        => GeoMath.DistanceMeters(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
}
