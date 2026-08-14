using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

/// <summary>
/// Request DTO for Valhalla's /trace_route endpoint.
/// Requires a "shape" (list of {lon, lat} objects), NOT "locations" — sending
/// locations returns HTTP 400. valhalla1.openstreetmap.de only accepts the
/// object form, not [lon,lat] coordinate-pair arrays (error 134).
/// shape_match=map_snap additionally requires trace_options.search_radius.
/// </summary>
public class TraceRequest
{
    [JsonPropertyName("shape")]
    public List<ShapePoint> Shape { get; set; } = [];

    [JsonPropertyName("costing")]
    public string Costing { get; set; } = "auto";

    [JsonPropertyName("costing_options")]
    public Dictionary<string, object>? CostingOptions { get; set; }

    /// <summary>
    /// Top-level trace matching algorithm. Must be set on the request root,
    /// NOT under costing_options (Valhalla ignores it there). "map_snap" is
    /// the right choice for imprecise GPS tracks.
    /// </summary>
    [JsonPropertyName("shape_match")]
    public string? ShapeMatch { get; set; }

    /// <summary>Required when shape_match=map_snap (missing trace_options → 400).</summary>
    [JsonPropertyName("trace_options")]
    public TraceOptions? TraceOptions { get; set; }
}

public class TraceOptions
{
    /// <summary>Max distance (meters) to look for a matching edge.</summary>
    [JsonPropertyName("search_radius")]
    public int SearchRadius { get; set; } = 50;
}

/// <summary>A single GPS trace point. Serialized as {"lon":…,"lat":…} (lon first).</summary>
public class ShapePoint
{
    public ShapePoint(double lon, double lat)
    {
        Lon = lon;
        Lat = lat;
    }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }

    [JsonPropertyName("lat")]
    public double Lat { get; set; }
}
