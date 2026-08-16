using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Closures;

/// <summary>
/// Element centroid returned by `out center` (used for ways, which have no
/// single coordinate). Used in the cheap first-pass candidate query.
/// </summary>
public class OverpassCenter
{
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }
}
