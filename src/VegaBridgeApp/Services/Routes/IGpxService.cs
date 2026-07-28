using VegaBridgeApp.Models.Routes;
using VegaBridgeApp.Models.Valhalla;

namespace VegaBridgeApp.Services.Routes;

/// <summary>
/// Service for importing and exporting routes in GPX 1.1 format.
/// </summary>
public interface IGpxService
{
    /// <summary>
    /// Parse a GPX stream into its structural parts (track + route).
    /// Use this when you need to inspect the contents before importing.
    /// </summary>
    Task<GpxParseResult> ParseGpxAsync(Stream gpxStream);

    /// <summary>
    /// Import a GPX stream and convert the chosen data to a SavedRoute.
    /// Call <see cref="ParseGpxAsync"/> first when the format is ambiguous.
    /// </summary>
    Task<SavedRoute?> ImportGpxAsync(Stream gpxStream);

    /// <summary>
    /// Export a SavedRoute to a GPX XML stream as track (&lt;trk&gt;).
    /// </summary>
    Task<Stream> ExportGpxAsync(SavedRoute route);

    /// <summary>
    /// Export a list of coordinates to a GPX XML stream.
    /// </summary>
    /// <param name="name">Route name.</param>
    /// <param name="waypoints">The points to export.</param>
    /// <param name="asTrack"><c>true</c> for &lt;trk&gt;, <c>false</c> for &lt;rte&gt;.</param>
    Task<Stream> ExportPointsAsync(string name, List<Coordinate> waypoints, bool asTrack = true);
}
