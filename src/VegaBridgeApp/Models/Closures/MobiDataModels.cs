using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Closures;

/// <summary>
/// Root of the MobiData BW roadworks GeoJSON feed (FeatureCollection).
/// </summary>
public class MobiDataFeed
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("features")]
    public List<MobiDataFeature>? Features { get; set; }
}

/// <summary>
/// A single feature of the MobiData BW roadworks GeoJSON feed
/// (https://api.mobidata-bw.de/datasets/traffic/roadworks/roadworks_geojson.json).
/// Only the fields used by the closure check are modeled.
/// </summary>
public class MobiDataFeature
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("geometry")]
    public MobiDataGeometry? Geometry { get; set; }

    [JsonPropertyName("properties")]
    public MobiDataProperties? Properties { get; set; }
}

/// <summary>
/// Geometry of a MobiData roadworks feature – a LineString of [lon, lat] pairs.
/// </summary>
public class MobiDataGeometry
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("coordinates")]
    public List<List<double>>? Coordinates { get; set; }
}

/// <summary>
/// Properties of a MobiData roadworks feature (official Baden-Württemberg
/// roadworks feed – the description text is the same one shown on
/// mobidata-bw.de / Google).
/// </summary>
public class MobiDataProperties
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>e.g. "ROAD_CLOSED" or "CONSTRUCTION".</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>e.g. "ROAD_CLOSED_CONSTRUCTION".</summary>
    [JsonPropertyName("subtype")]
    public string? Subtype { get; set; }

    /// <summary>Human-readable description, e.g. "B313 von Großbettlingen Erneuerung an der Decke".</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Always "MobiData BW" in the feed.</summary>
    [JsonPropertyName("reference")]
    public string? Reference { get; set; }

    /// <summary>Road label, e.g. "B313 Grafenberg-Nürtingen".</summary>
    [JsonPropertyName("street")]
    public string? Street { get; set; }

    /// <summary>e.g. "BOTH_DIRECTIONS" or "ONE_DIRECTION".</summary>
    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    /// <summary>ISO-8601 validity start, e.g. "2026-08-14T00:00:00.000+02:00".</summary>
    [JsonPropertyName("starttime")]
    public string? StartTime { get; set; }

    /// <summary>ISO-8601 validity end.</summary>
    [JsonPropertyName("endtime")]
    public string? EndTime { get; set; }
}
