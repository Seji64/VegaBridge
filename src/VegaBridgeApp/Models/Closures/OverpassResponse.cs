using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Closures;

/// <summary>
/// Root response of an Overpass API query ([out:json]).
/// </summary>
public class OverpassResponse
{
    [JsonPropertyName("elements")]
    public List<OverpassElement> Elements { get; set; } = [];
}
