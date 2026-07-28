using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

public class IntersectingEdge
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("begin_restriction")]
    public bool? BeginRestriction { get; set; }

    [JsonPropertyName("end_restriction")]
    public bool? EndRestriction { get; set; }

    [JsonPropertyName("driveability")]
    public int? Driveability { get; set; }

    [JsonPropertyName("cyclability")]
    public int? Cyclability { get; set; }

    [JsonPropertyName("walkability")]
    public int? Walkability { get; set; }

    [JsonPropertyName("use")]
    public string? Use { get; set; }

    [JsonPropertyName("road_class")]
    public string? RoadClass { get; set; }
}
