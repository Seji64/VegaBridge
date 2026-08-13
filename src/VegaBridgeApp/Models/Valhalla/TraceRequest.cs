using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

/// <summary>
/// Request DTO specifically for Valhalla's /trace_route endpoint.
/// This endpoint requires locations as a list of coordinate pairs [lat, lon].
/// </summary>
public class TraceRequest
{
    [JsonPropertyName("locations")]
    public List<double[]> Locations { get; set; } = [];

    [JsonPropertyName("costing")]
    public string Costing { get; set; } = "auto";

    [JsonPropertyName("costing_options")]
    public Dictionary<string, object>? CostingOptions { get; set; }
}
