using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Closures;

/// <summary>
/// A single OSM element returned by Overpass (type, id, tags, geometry).
/// Only the fields used by the closure check are modeled.
/// </summary>
public class OverpassElement
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>Full way geometry when queried with `out geom`.</summary>
    [JsonPropertyName("geometry")]
    public List<OverpassGeometryPoint>? Geometry { get; set; }

    /// <summary>Way centroid from the cheap `out center` first-pass query.</summary>
    [JsonPropertyName("center")]
    public OverpassCenter? Center { get; set; }

    [JsonPropertyName("tags")]
    public OverpassTags? Tags { get; set; }

    /// <summary>ISO-8601 string (e.g. "2026-02-26T20:44:46Z").</summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}
