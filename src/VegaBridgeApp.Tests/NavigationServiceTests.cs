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

    private const int Depart = 1, Arrive = 4, Right = 10;

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

        // Rider is always on "the route road" (way 1) – only wrong-way can
        // make it off route. Reroutes fail (no Valhalla in tests).
        _valhalla.LocateAsync(Arg.Any<List<(double, double)>>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((List<(double, double)>)ci[0])
                .Select(_ => (LocateResponse?)new LocateResponse { Edges = [new LocateEdge { WayId = 1, PercentAlong = 0.5 }] })
                .ToList());
        _valhalla.GetRouteAsync(Arg.Any<RouteRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("no route in tests"));

        _nav = new NavigationService(_gps, _valhalla);
        _nav.AddSink(_sink);
    }

    [Fact]
    public async Task RoundTrip_FirstFixAtStart_DoesNotFinishImmediately()
    {
        await StartAsync(Leg(Depart, A, B, C, D, A));

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
        await StartAsync(Leg(Depart, A, B, C, D, A));

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
        await StartAsync(Leg(Depart, A, B, C, D, A));
        foreach (Coordinate p in Ride(A, Lerp(A, B, 0.4)))
            Fix(p, speedMs: 14);

        // Turn around on the same road: the way_id still matches, which used
        // to cancel the wrong-way detection forever.
        Coordinate here = Lerp(A, B, 0.4);
        for (int i = 1; i <= 6 && !_sink.OffRoute; i++)
        {
            Fix(new Coordinate(here.Latitude - i * 0.0002, here.Longitude, null), speedMs: 14, headingDeg: 180);
            Thread.Sleep(i >= 3 ? 2100 : 0); // locate/off-route checks are throttled to one per 2 s
        }

        Assert.True(_sink.OffRoute);
    }

    [Fact]
    public async Task Reroute_KeepsOnlyWaypointsAhead()
    {
        // Waypoints B and D as stops (one leg each, like Map.StartNavigation).
        await StartAsync(
            [Leg(Depart, A, B), Leg(Depart, B, C, D), Leg(Depart, D, A)],
            vias: [Loc(B), Loc(D)]);

        Coordinate pos = Lerp(B, C, 0.5);
        foreach (Coordinate p in Ride(A, B, pos))
            Fix(p);

        await _nav.PerformRerouteAsync(pos.Latitude, pos.Longitude, skipNextWaypoint: false);

        RouteRequest request = (RouteRequest)_valhalla.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IValhallaClient.GetRouteAsync))
            .GetArguments()[0]!;
        // current position → D (B already passed) → destination A
        Assert.Equal(3, request.Locations!.Count);
        Assert.Equal(D.Latitude, request.Locations[1].Lat, 6);
        Assert.Equal(D.Longitude, request.Locations[1].Lon, 6);
        Assert.Equal("break", request.Locations[1].Type);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private Task StartAsync(Leg leg) => StartAsync([leg], []);

    private async Task StartAsync(List<Leg> legs, List<Location> vias)
    {
        (string shape, List<Maneuver> maneuvers, double km, double min) = _nav.PrepareNavigationData(legs);
        await _nav.StartNavigation(shape, maneuvers, km, min, Loc(A), vias);
    }

    /// <summary>One leg: depart, a right turn at every inner point, arrive.</summary>
    private static Leg Leg(int departType, params Coordinate[] points)
    {
        List<Maneuver> maneuvers = [new() { Type = departType, BeginShapeIndex = 0, EndShapeIndex = 1 }];
        for (int i = 1; i < points.Length - 1; i++)
            maneuvers.Add(new Maneuver { Type = Right, BeginShapeIndex = i, EndShapeIndex = i + 1 });
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

    /// <summary>Points every ~50 m along the polyline.</summary>
    private static List<Coordinate> Ride(params Coordinate[] points)
    {
        List<Coordinate> ride = [];
        for (int i = 1; i < points.Length; i++)
        {
            double m = GeoMath.DistanceMeters(points[i - 1].Latitude, points[i - 1].Longitude, points[i].Latitude, points[i].Longitude);
            int steps = Math.Max(1, (int)(m / 50));
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
