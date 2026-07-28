using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

public class Maneuver
{
    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("instruction")]
    public string? Instruction { get; set; }

    [JsonPropertyName("verbal_succinct_transition_instruction")]
    public string? VerbalSuccinctInstruction { get; set; }

    [JsonPropertyName("verbal_pre_transition_instruction")]
    public string? VerbalPreInstruction { get; set; }

    [JsonPropertyName("verbal_post_transition_instruction")]
    public string? VerbalPostInstruction { get; set; }

    [JsonPropertyName("street_names")]
    public List<string>? StreetNames { get; set; }

    [JsonPropertyName("length")]
    public double Length { get; set; }

    [JsonPropertyName("time")]
    public double Time { get; set; }

    [JsonPropertyName("cost")]
    public double? Cost { get; set; }

    [JsonPropertyName("begin_shape_index")]
    public int BeginShapeIndex { get; set; }

    [JsonPropertyName("end_shape_index")]
    public int EndShapeIndex { get; set; }

    [JsonPropertyName("travel_mode")]
    public string? TravelMode { get; set; }

    [JsonPropertyName("travel_type")]
    public string? TravelType { get; set; }

    [JsonPropertyName("verbal_multi_cue")]
    public bool VerbalMultiCue { get; set; }

    [JsonPropertyName("turn_degree")]
    public double? TurnDegree { get; set; }

    [JsonPropertyName("sign")]
    public Sign? Sign { get; set; }

    [JsonPropertyName("roundabout_exit_count")]
    public int? RoundaboutExitCount { get; set; }

    [JsonPropertyName("roundabout_exit")]
    public int? RoundaboutExit { get; set; }

    [JsonPropertyName("internal_intersection_index")]
    public int? InternalIntersectionIndex { get; set; }
}
