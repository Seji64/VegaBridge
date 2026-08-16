using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Closures;

/// <summary>
/// OSM tags relevant to the closure check.
/// </summary>
public class OverpassTags
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("highway")]
    public string? Highway { get; set; }

    [JsonPropertyName("construction")]
    public string? Construction { get; set; }

    [JsonPropertyName("access")]
    public string? Access { get; set; }

    [JsonPropertyName("barrier")]
    public string? Barrier { get; set; }
}
