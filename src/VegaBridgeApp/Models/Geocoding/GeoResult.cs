namespace VegaBridgeApp.Models.Geocoding;

/// <summary>
/// A geocoding result (user-selected or suggested by Photon/Komoot).
/// </summary>
public record GeoResult(
    string Label,     // Display text, e.g. "Stuttgart, Baden‑Württemberg, Germany"
    double Latitude,
    double Longitude,
    string? Type = null   // city, street, venue, …
)
{
    /// <summary>
    /// Returns a human-readable single-line representation.
    /// </summary>
    public override string ToString() => Label;
    
    public bool GUIDummy { get; set; } 
}
