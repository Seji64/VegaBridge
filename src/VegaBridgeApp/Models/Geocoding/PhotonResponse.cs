using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Geocoding;

public class PhotonResponse
{
    [JsonPropertyName("features")]
    public List<PhotonFeature> Features { get; set; } = [];
}
