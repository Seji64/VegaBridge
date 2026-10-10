using NSubstitute;
using Shiny;
using Shiny.Locations;
using VegaBridgeApp.Models.Navigation;
using VegaBridgeApp.Models.Valhalla;
using VegaBridgeApp.Services.Location;
using VegaBridgeApp.Services.Navigation;
using VegaBridgeApp.Services.Routes;
using VegaBridgeApp.Services.Valhalla;
using VegaBridgeApp.Utils;
using Xunit;
using Coordinate = VegaBridgeApp.Models.Valhalla.Coordinate;
using Location = VegaBridgeApp.Models.Valhalla.Location;

namespace VegaBridgeApp.Tests;

/// <summary>
/// Drives <see cref="NavigationService"/> with simulated GPS fixes along a
/// ~4 km square round trip A → B → C → D → A (start == destination).
/// </summary>
public class NavigationServiceTests
{
    private static readonly Coordinate A = new(48.0000, 9.0000, null);
    private static readonly Coordinate B = new(48.0090, 9.0000, null); // ~1 km north
    private static readonly Coordinate C = new(48.0090, 9.0134, null); // ~1 km east
    private static readonly Coordinate D = new(48.0000, 9.0134, null);

    private const int Depart = 1, Arrive = 4, Right = 10, Left = 15;
    private const double LonPerMeter = 1 / 74_490.0; // at 48° N

    private readonly GpsService _gps;
    private readonly IValhallaClient _valhalla = Substitute.For<IValhallaClient>();
    private readonly NavigationService _nav;
    private readonly TestSink _sink = new();
    private DateTimeOffset _time = DateTimeOffset.UtcNow;

    public NavigationServiceTests()
    {
        IGpsManager gpsManager = Substitute.For<IGpsManager>();
        gpsManager.GetCurrentStatus(Arg.Any<GpsRequest>()).Returns(AccessState.Available);
        _gps = new GpsService(gpsManager);

        // By default the rider is on "the route road" (way 1) wherever GPS
        // puts them. Reroutes fail (no Valhalla in tests).
        LocateWay((_, _) => 1);
        _valhalla.GetRouteAsync(Arg.Any<RouteRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("no route in tests"));

        _nav = new NavigationService(_gps, _valhalla);
        _nav.AddSink(_sink);
    }

    [Fact]
    public async Task RoundTrip_FirstFixAtStart_DoesNotFinishImmediately()
    {
        await StartAsync(Leg(Right, A, B, C, D, A));

        // 7 m east of A – on the final D→A segment, i.e. nearer to the route
        // END than to its start. Used to snap there → "destination reached".
        Fix(new Coordinate(48.0000, 9.0001, null));
        Fix(new Coordinate(48.0005, 9.0000, null));

        Assert.True(_nav.IsNavigating);
        Assert.True(_sink.LastStatus!.RemainingDistanceKm > 3.5, $"remaining {_sink.LastStatus.RemainingDistanceKm:F2} km");
    }

    [Fact]
    public async Task RoundTrip_RideWholeLoop_FinishesOnlyAtTheEnd()
    {
        await StartAsync(Leg(Right, A, B, C, D, A));

        List<Coordinate> ride = Ride(A, B, C, D, A);
        foreach (Coordinate p in ride)
        {
            Fix(p);
            Assert.True(_nav.IsNavigating, $"finished early at {p}");
        }

        // Standing at the destination: the smoothed position catches up.
        for (int i = 0; i < 3 && _nav.IsNavigating; i++)
            Fix(A);

        Assert.False(_nav.IsNavigating);
        await _sink.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UTurnOnRouteRoad_IsDetectedAsOffRoute()
    {
        await StartAsync(Leg(Right, A, B, C, D, A));
        foreach (Coordinate p in Ride(A, Lerp(A, B, 0.4)))
            Fix(p, speedMs: 14);

        // Turn around on the same road: the way_id still matches, which used
        // to cancel the wrong-way detection forever.
        Coordinate here = Lerp(A, B, 0.4);
        for (int i = 1; i <= 6 && !_sink.OffRoute; i++)
            Fix(new Coordinate(here.Latitude - i * 0.0002, here.Longitude, null), speedMs: 14, headingDeg: 180);

        Assert.True(_sink.OffRoute);
    }

    [Fact]
    public async Task Reroute_KeepsOnlyWaypointsAhead()
    {
        // Waypoints B and D as stops (one leg each, like Map.StartNavigation).
        await StartAsync(
            [Leg(Right, A, B), Leg(Right, B, C, D), Leg(Right, D, A)],
            vias: [Loc(B), Loc(D)]);

        Coordinate pos = Lerp(B, C, 0.5);
        foreach (Coordinate p in Ride(A, B, pos))
            Fix(p);

        await _nav.PerformRerouteAsync(pos.Latitude, pos.Longitude, skipNextWaypoint: false);

        RouteRequest request = LastRouteRequest();
        // current position → D (B already passed) → destination A
        Assert.Equal(3, request.Locations!.Count);
        Assert.Equal(D.Latitude, request.Locations[1].Lat, 6);
        Assert.Equal(D.Longitude, request.Locations[1].Lon, 6);
        Assert.Equal("break", request.Locations[1].Type);
    }

    [Fact]
    public async Task WrongTurnInCity_IsRerouted_WithinSeconds()
    {
        // Route: 330 m north to X, then LEFT (west). Rider turns RIGHT (east).
        // 36 km/h, a fix every 10 m.
        Coordinate x = Lerp(A, B, 1.0 / 3);
        Coordinate west = new(x.Latitude, x.Longitude - 300 * LonPerMeter, null);
        await StartAsync(Leg(Left, A, x, west));
        LocateWay((_, lon) => lon > x.Longitude + 15 * LonPerMeter ? 3 : 1); // east street = way 3

        foreach (Coordinate p in Ride(10, A, x))
            Fix(p, speedMs: 10, headingDeg: 0);
        Assert.False(_sink.OffRoute);

        int seconds = 0;
        Coordinate pos = x;
        while (!RerouteRequested() && seconds < 30)
        {
            seconds++;
            pos = new Coordinate(x.Latitude, x.Longitude + seconds * 10 * LonPerMeter, null);
            Fix(pos, speedMs: 10, headingDeg: 90);
        }

        Assert.True(_sink.OffRoute);
        Assert.True(seconds <= 6, $"reroute after {seconds} s / {seconds * 10} m");
        // New route starts where the rider is now, in their direction.
        Location origin = LastRouteRequest().Locations[0];
        Assert.Equal(pos.Latitude, origin.Lat, 6);
        Assert.Equal(pos.Longitude, origin.Lon, 6);
        Assert.Equal(90, origin.Heading);
    }

    [Fact]
    public async Task ParallelRoad_IsDetectedAsOffRoute()
    {
        // Route on the motorway (straight north); the rider takes the parallel
        // road 40 m east of it. 120 km/h, a fix every 33 m.
        await StartAsync(Leg(Right, A, B));
        LocateWay((_, lon) => lon > A.Longitude + 25 * LonPerMeter ? 2 : 1);

        List<Coordinate> motorway = Ride(33, A, Lerp(A, B, 0.3));
        foreach (Coordinate p in motorway)
            Fix(p, speedMs: 33, headingDeg: 0);
        Assert.False(_sink.OffRoute);

        // Exit ramp drifts east over ~100 m, then runs parallel at 40 m.
        Coordinate last = motorway[^1];
        int seconds = 0;
        while (!RerouteRequested() && seconds < 30)
        {
            seconds++;
            double east = Math.Min(40, seconds * 13.5);
            Fix(new Coordinate(last.Latitude + seconds * 33 / 111_320.0, last.Longitude + east * LonPerMeter, null),
                speedMs: 33, headingDeg: 0);
        }

        Assert.True(_sink.OffRoute);
        Assert.True(seconds <= 8, $"reroute after {seconds} s / {seconds * 33} m");
    }

    [Fact]
    public async Task GpsDriftInCity_OnTheRouteRoad_IsNotOffRoute()
    {
        // Urban canyon: GPS puts the rider 35 m beside the route for 15 s,
        // but locate finds them on the route road (default fake) – no reroute.
        await StartAsync(Leg(Right, A, B));
        List<Coordinate> ride = Ride(10, A, Lerp(A, B, 0.5));
        for (int i = 0; i < ride.Count; i++)
        {
            bool drift = i is >= 20 and < 35;
            Coordinate p = drift ? new Coordinate(ride[i].Latitude, ride[i].Longitude + 35 * LonPerMeter, null) : ride[i];
            Fix(p, speedMs: 10, headingDeg: 0);
        }

        Assert.False(_sink.OffRoute);
        Assert.False(RerouteRequested());
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private Task StartAsync(Leg leg) => StartAsync([leg], []);

    private async Task StartAsync(List<Leg> legs, List<Location> vias)
    {
        (string shape, List<Maneuver> maneuvers, double km, double min) = _nav.PrepareNavigationData(legs);
        await _nav.StartNavigation(shape, maneuvers, km, min, Loc(A), vias);
    }

    /// <summary>One leg: depart, a <paramref name="turnType"/> turn at every inner point, arrive.</summary>
    private static Leg Leg(int turnType, params Coordinate[] points)
    {
        List<Maneuver> maneuvers = [new() { Type = Depart, BeginShapeIndex = 0, EndShapeIndex = 1 }];
        for (int i = 1; i < points.Length - 1; i++)
            maneuvers.Add(new Maneuver { Type = turnType, BeginShapeIndex = i, EndShapeIndex = i + 1 });
        maneuvers.Add(new Maneuver { Type = Arrive, BeginShapeIndex = points.Length - 1, EndShapeIndex = points.Length - 1 });

        double km = 0;
        for (int i = 1; i < points.Length; i++)
            km += GeoMath.DistanceKm(points[i - 1].Latitude, points[i - 1].Longitude, points[i].Latitude, points[i].Longitude);

        return new Leg
        {
            Shape = PolylineEncoder.EncodePolyline6([.. points]),
            Maneuvers = maneuvers,
            Summary = new Summary { Length = km, Time = km * 60 }
        };
    }

    private static List<Coordinate> Ride(params Coordinate[] points) => Ride(50, points);

    /// <summary>Points every ~<paramref name="stepM"/> along the polyline.</summary>
    private static List<Coordinate> Ride(double stepM, params Coordinate[] points)
    {
        List<Coordinate> ride = [];
        for (int i = 1; i < points.Length; i++)
        {
            double m = GeoMath.DistanceMeters(points[i - 1].Latitude, points[i - 1].Longitude, points[i].Latitude, points[i].Longitude);
            int steps = Math.Max(1, (int)(m / stepM));
            for (int s = 1; s <= steps; s++)
                ride.Add(Lerp(points[i - 1], points[i], (double)s / steps));
        }
        return ride;
    }

    private void Fix(Coordinate p, double speedMs = 0, double headingDeg = -1)
    {
        _time = _time.AddSeconds(1);
        _gps.PublishSimulated(new GpsReading(
            new Position(p.Latitude, p.Longitude),
            PositionAccuracy: 5,
            Timestamp: _time,
            Heading: headingDeg,
            HeadingAccuracy: 10,
            Altitude: 0,
            Speed: speedMs,
            SpeedAccuracy: 1,
            IsStationary: speedMs == 0));
    }

    /// <summary>Fake Valhalla locate: which OSM way is at a position.</summary>
    private void LocateWay(Func<double, double, long> wayAt) =>
        _valhalla.LocateAsync(Arg.Any<List<(double, double)>>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((List<(double Lat, double Lon)>)ci[0])
                .Select(p => (LocateResponse?)new LocateResponse { Edges = [new LocateEdge { WayId = wayAt(p.Lat, p.Lon), PercentAlong = 0.5 }] })
                .ToList());

    private bool RerouteRequested() => RouteRequests().Any();

    private RouteRequest LastRouteRequest() => RouteRequests().Last();

    private IEnumerable<RouteRequest> RouteRequests() => _valhalla.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name == nameof(IValhallaClient.GetRouteAsync))
        .Select(c => (RouteRequest)c.GetArguments()[0]!);

    private static Coordinate Lerp(Coordinate a, Coordinate b, double t) =>
        new(a.Latitude + t * (b.Latitude - a.Latitude), a.Longitude + t * (b.Longitude - a.Longitude), null);

    private static Location Loc(Coordinate c) => new() { Lat = c.Latitude, Lon = c.Longitude, Type = "break" };

    private sealed class TestSink : INavigationSink
    {
        public NavigationStatus? LastStatus;
        public bool OffRoute;
        public readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OnStatusAsync(NavigationStatus status) { LastStatus = status; return Task.CompletedTask; }
        public Task OnOffRouteAsync(double latitude, double longitude, double distanceMeters) { OffRoute = true; return Task.CompletedTask; }
        public Task OnFinishAsync() { Finished.TrySetResult(); return Task.CompletedTask; }
        public Task OnStartAsync(NavigationStartInfo start) => Task.CompletedTask;
        public Task OnManeuverAsync(NavigationManeuverInfo maneuver) => Task.CompletedTask;
        public Task OnCancelAsync() => Task.CompletedTask;
        public Task OnRouteUpdatedAsync(RouteResponse response) => Task.CompletedTask;
    }
}
