using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

public class Summary
{
    [JsonPropertyName("length")]
    public double Length { get; set; }

    [JsonPropertyName("time")]
    public double Time { get; set; }

    [JsonPropertyName("min_lat")]
    public double? MinLat { get; set; }

    [JsonPropertyName("min_lon")]
    public double? MinLon { get; set; }

    [JsonPropertyName("max_lat")]
    public double? MaxLat { get; set; }

    [JsonPropertyName("max_lon")]
    public double? MaxLon { get; set; }

    [JsonPropertyName("has_highway")]
    public bool HasHighway { get; set; }

    [JsonPropertyName("has_toll")]
    public bool HasToll { get; set; }

    [JsonPropertyName("has_ferry")]
    public bool HasFerry { get; set; }

    /// <summary>The avoidable road types this route uses.</summary>
    [JsonIgnore]
    public RoadAvoidance AvoidableRoads =>
        (HasHighway ? RoadAvoidance.Highways : RoadAvoidance.None)
        | (HasToll ? RoadAvoidance.Tolls : RoadAvoidance.None)
        | (HasFerry ? RoadAvoidance.Ferries : RoadAvoidance.None);
}
