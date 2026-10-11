using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

/// <summary>
/// Request DTO for Valhalla's /trace_attributes endpoint on a route shape.
/// "edge_walk" walks the exact edges of a Valhalla route shape (no map
/// matching) and returns its points unchanged, so the edges' shape indices
/// are indices into <see cref="EncodedPolyline"/>. It fails on a U-turn at a
/// waypoint – send one leg at a time.
/// </summary>
public class TraceAttributesRequest
{
    /// <summary>Polyline6-encoded shape.</summary>
    [JsonPropertyName("encoded_polyline")]
    public string EncodedPolyline { get; set; } = string.Empty;

    [JsonPropertyName("shape_match")]
    public string ShapeMatch { get; set; } = "edge_walk";

    [JsonPropertyName("costing")]
    public string Costing { get; set; } = "motorcycle";

    [JsonPropertyName("filters")]
    public TraceAttributesFilter? Filters { get; set; }
}

/// <summary>Limits the response to the listed attributes (e.g. "edge.tunnel").</summary>
public class TraceAttributesFilter
{
    [JsonPropertyName("attributes")]
    public List<string> Attributes { get; set; } = [];

    [JsonPropertyName("action")]
    public string Action { get; set; } = "include";
}

public class TraceAttributesResponse
{
    [JsonPropertyName("edges")]
    public List<TraceEdge>? Edges { get; set; }
}

public class TraceEdge
{
    [JsonPropertyName("tunnel")]
    public bool Tunnel { get; set; }

    [JsonPropertyName("begin_shape_index")]
    public int BeginShapeIndex { get; set; }

    [JsonPropertyName("end_shape_index")]
    public int EndShapeIndex { get; set; }
}
