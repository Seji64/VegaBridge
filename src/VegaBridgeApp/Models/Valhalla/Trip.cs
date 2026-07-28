using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

public class Trip
{
    [JsonPropertyName("locations")]
    public List<Location>? Locations { get; set; }

    [JsonPropertyName("legs")]
    public List<Leg>? Legs { get; set; }

    [JsonPropertyName("summary")]
    public Summary? Summary { get; set; }

    [JsonPropertyName("status")]
    public int? Status { get; set; }

    [JsonPropertyName("status_message")]
    public string? StatusMessage { get; set; }
}
