using System.Globalization;
using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

public class DirectionsOptions
{
    [JsonPropertyName("units")]
    public string Units { get; set; } = "kilometers";

    [JsonPropertyName("language")]
    // App UI is German or English (Resources/App*.resx) – match the instructions to it.
    public string? Language { get; set; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en";
}
