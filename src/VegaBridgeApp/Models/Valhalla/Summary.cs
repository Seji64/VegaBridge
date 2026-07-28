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
}
