using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Geocoding;

public class PhotonGeometry
{
    [JsonPropertyName("coordinates")]
    public List<double> Coordinates { get; set; } = [];   // [lon, lat]
}
