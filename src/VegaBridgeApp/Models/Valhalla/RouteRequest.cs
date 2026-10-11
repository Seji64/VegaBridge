using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

/// <summary>
/// Request DTO for Valhalla's /route endpoint.
/// </summary>
public class RouteRequest
{
    [JsonPropertyName("locations")]
    public List<Location> Locations { get; set; } = [];

    [JsonPropertyName("costing")]
    public string Costing { get; set; } = "auto";

    /// <summary>Keyed by costing name, see <c>CostingOptions.Avoiding</c>.</summary>
    [JsonPropertyName("costing_options")]
    public Dictionary<string, CostingOptions>? CostingOptions { get; set; }

    [JsonPropertyName("directions_options")]
    public DirectionsOptions? DirectionsOptions { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("units")]
    public string? Units { get; set; }
}
