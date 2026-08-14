using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

public class Location
{
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }

    /// <summary>
    /// "break" (default), "through", "via", "break_through"
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Preferred direction of travel at this location (0-359°, 0 = north,
    /// clockwise). Valhalla filters candidate edges by this heading, so a
    /// reroute starts in the rider's actual travel direction instead of
    /// picking an arbitrary first edge (e.g. a 180° turnaround).
    /// </summary>
    [JsonPropertyName("heading")]
    public double? Heading { get; set; }

    /// <summary>
    /// Max angle between the given heading and a candidate edge (default 60°).
    /// Narrower = stricter direction match.
    /// </summary>
    [JsonPropertyName("heading_tolerance")]
    public double? HeadingTolerance { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("street")]
    public string? Street { get; set; }
}
