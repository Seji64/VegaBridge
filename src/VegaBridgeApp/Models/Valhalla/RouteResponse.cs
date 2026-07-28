using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

/// <summary>
/// Response DTO from Valhalla's /route endpoint.
/// </summary>
public class RouteResponse
{
    [JsonPropertyName("trip")]
    public Trip? Trip { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}
