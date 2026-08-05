using System.Xml.Linq;
using Serilog;
using VegaBridgeApp.Models.Routes;
using VegaBridgeApp.Utils;
using Coordinate = VegaBridgeApp.Models.Valhalla.Coordinate;

namespace VegaBridgeApp.Services.Routes;

/// <summary>
/// GPX 1.1 import/export using plain LINQ to XML (KISS).
/// Handles both &lt;trk&gt; (track) and &lt;rte&gt; (route) elements.
/// </summary>
public class GpxService : IGpxService
{
    private static readonly XNamespace GpxNs = "http://www.topografix.com/GPX/1/1";
    
    // ── Parse ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<GpxParseResult> ParseGpxAsync(Stream gpxStream)
    {
        try
        {
            XDocument doc = XDocument.Load(gpxStream);
            XElement? gpx = doc.Root;
            if (gpx == null || (gpx.Name != GpxNs + "gpx" && gpx.Name != "gpx"))
                return Task.FromResult(new GpxParseResult());

            // Detect namespace: some GPX files (Apple Simulator) omit the GPX namespace
            XNamespace ns = gpx.Name.Namespace;

            // Parse track (<trk>)
            List<Coordinate>? trackPoints = null;
            string trackName = "";

            XElement? track = gpx.Element(ns + "trk");
            if (track != null)
            {
                trackName = track.Element(ns + "name")?.Value ?? "";
                // Try <trkseg> first, fall back to direct <trkpt> children (Apple Simulator GPX)
                XElement? segment = track.Element(ns + "trkseg");
                IEnumerable<XElement> trackPtElements = segment != null
                    ? segment.Elements(ns + "trkpt")
                    : track.Elements(ns + "trkpt");
                trackPoints = trackPtElements
                    .Select(p => ParsePoint(p, ns))
                    .Where(static wp => wp != null)
                    .Cast<Coordinate>()
                    .ToList();
            }

            // Parse route (<rte>)
            List<Coordinate>? routePoints = null;
            string routeName = "";

            XElement? route = gpx.Element(ns + "rte");
            if (route != null)
            {
                routeName = route.Element(ns + "name")?.Value ?? "";
                routePoints = route
                    .Elements(ns + "rtept")
                    .Select(p => ParsePoint(p, ns))
                    .Where(static wp => wp != null)
                    .Cast<Coordinate>()
                    .ToList();
            }

            Log.Debug("GPX parse: trk={Trk}({TrkPts}pts) rte={Rte}({RtePts}pts)",
                trackPoints?.Count >= 2, trackPoints?.Count ?? 0,
                routePoints?.Count >= 2, routePoints?.Count ?? 0);

            return Task.FromResult(new GpxParseResult
            {
                TrackPoints = trackPoints,
                TrackName = trackName,
                RoutePoints = routePoints,
                RouteName = routeName
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to parse GPX");
            return Task.FromResult(new GpxParseResult());
        }
    }

    /// <inheritdoc />
    public Task<SavedRoute?> ImportGpxAsync(Stream gpxStream)
    {
        // For simple import without UI decision, prefer track over route
        GpxParseResult parsed = ParseGpxAsync(gpxStream).Result;

        List<Coordinate>? points = parsed.TrackPoints ?? parsed.RoutePoints;
        string name = parsed.HasTrack ? parsed.TrackName : parsed.RouteName;

        if (points == null || points.Count < 2)
            return Task.FromResult<SavedRoute?>(null);

        return Task.FromResult<SavedRoute?>(BuildSavedRoute(name, points));
    }

    // ── Export ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<Stream> ExportGpxAsync(SavedRoute route)
    {
        return ExportPointsAsync(
            route.Name ?? "Route",
            route.Waypoints ?? [],
            asTrack: true);
    }

    /// <inheritdoc />
    public Task<Stream> ExportPointsAsync(string name, List<Coordinate> waypoints, bool asTrack = true)
    {
        XElement content = asTrack
            ? BuildTrackElement(name, waypoints)
            : BuildRouteElement(name, waypoints);

        XDocument doc = new(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(GpxNs + "gpx",
                new XAttribute("version", "1.1"),
                new XAttribute("creator", "VegaBridgeApp"),
                content
            )
        );

        MemoryStream stream = new();
        doc.Save(stream, SaveOptions.DisableFormatting);
        stream.Position = 0;
        return Task.FromResult<Stream>(stream);
    }

    // ── XML builders ───────────────────────────────────────────────────

    private static XElement BuildTrackElement(string name, List<Coordinate> points) =>
        new(GpxNs + "trk",
            new XElement(GpxNs + "name", name),
            new XElement(GpxNs + "trkseg",
                points.Select(wp =>
                    new XElement(GpxNs + "trkpt",
                        new XAttribute("lat", wp.Latitude),
                        new XAttribute("lon", wp.Longitude),
                        wp.Label != null ? new XElement(GpxNs + "name", wp.Label) : null
                    )
                )
            )
        );

    private static XElement BuildRouteElement(string name, List<Coordinate> points) =>
        new(GpxNs + "rte",
            new XElement(GpxNs + "name", name),
            points.Select(wp =>
                new XElement(GpxNs + "rtept",
                    new XAttribute("lat", wp.Latitude),
                    new XAttribute("lon", wp.Longitude),
                    wp.Label != null ? new XElement(GpxNs + "name", wp.Label) : null
                )
            )
        );

    // ── Helpers ─────────────────────────────────────────────────────────

    private static Coordinate? ParsePoint(XElement pt, XNamespace ns)
    {
        double? lat = (double?)pt.Attribute("lat");
        double? lon = (double?)pt.Attribute("lon");
        if (lat == null || lon == null) return null;

        string? label = pt.Element(ns + "name")?.Value
                     ?? pt.Element(ns + "desc")?.Value;

        return new Coordinate(lat.Value, lon.Value, label);
    }

    private static SavedRoute BuildSavedRoute(string name, List<Coordinate> points)
    {
        string polyline6 = PolylineEncoder.EncodePolyline6(points);
        double totalKm = GeoMath.TotalDistanceKm(points);

        return new SavedRoute
        {
            Name = name,
            Waypoints = points,
            Polyline6 = polyline6,
            DistanceKm = Math.Round(totalKm, 2),
            TimeMinutes = Math.Round((totalKm / 50.0) * 60.0, 1),
            CreatedAt = DateTime.UtcNow
        };
    }

}
