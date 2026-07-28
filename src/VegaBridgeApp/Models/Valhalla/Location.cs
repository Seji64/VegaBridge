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

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("street")]
    public string? Street { get; set; }
}
