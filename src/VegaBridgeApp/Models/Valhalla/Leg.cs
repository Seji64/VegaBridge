using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

public class Leg
{
    [JsonPropertyName("maneuvers")]
    public List<Maneuver>? Maneuvers { get; set; }

    [JsonPropertyName("summary")]
    public Summary? Summary { get; set; }

    /// <summary>
    /// Polyline6-encoded shape of this leg.
    /// </summary>
    [JsonPropertyName("shape")]
    public string? Shape { get; set; }

    [JsonPropertyName("internal_intersections")]
    public List<Intersection>? InternalIntersections { get; set; }
}
