using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Geocoding;

public class PhotonFeature
{
    [JsonPropertyName("geometry")]
    public PhotonGeometry Geometry { get; set; } = new();

    [JsonPropertyName("properties")]
    public PhotonProperties Properties { get; set; } = new();
}
