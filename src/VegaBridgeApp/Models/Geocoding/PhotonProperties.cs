using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Geocoding;

public class PhotonProperties
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("street")]
    public string? Street { get; set; }

    [JsonPropertyName("housenumber")]
    public string? Housenumber { get; set; }

    [JsonPropertyName("postcode")]
    public string? Postcode { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    /// <summary>city, locality, district, street, venue, …</summary>
    [JsonPropertyName("osm_value")]
    public string? OsmValue { get; set; }

    [JsonPropertyName("osm_key")]
    public string? OsmKey { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("extent")]
    public List<double>? Extent { get; set; }
}
