using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

public class Intersection
{
    [JsonPropertyName("lon")]
    public double Lon { get; set; }

    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("bearing")]
    public double? Bearing { get; set; }

    [JsonPropertyName("turn_lanes")]
    public List<string>? TurnLanes { get; set; }

    [JsonPropertyName("admin_index")]
    public int? AdminIndex { get; set; }

    [JsonPropertyName("intersecting_edge")]
    public IntersectingEdge? IntersectingEdge { get; set; }
}
