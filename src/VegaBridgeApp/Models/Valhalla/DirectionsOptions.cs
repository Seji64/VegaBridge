using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

public class DirectionsOptions
{
    [JsonPropertyName("units")]
    public string Units { get; set; } = "kilometers";

    [JsonPropertyName("language")]
    public string? Language { get; set; }
}
