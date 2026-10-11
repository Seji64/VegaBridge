namespace VegaBridgeApp.Models.Geocoding;

/// <summary>
/// A geocoding result (user-selected or suggested by Photon/Komoot).
/// </summary>
public record GeoResult(
    string Label,     // Display text, e.g. "Stuttgart, Baden‑Württemberg, Germany"
    double Latitude,
    double Longitude,
    string? Type = null,  // city, street, venue, …
    string? Place = null  // place part of the label, e.g. "Berlin" for "Klosterstraße, Parochialstraße, Berlin"
)
{
    private const string Separator = ", ";

    /// <summary>
    /// Returns a human-readable single-line representation.
    /// </summary>
    public override string ToString() => Label;
    
    public bool GUIDummy { get; set; } 

    /// <summary>First label segment (POI name or street) – the main suggestion line.</summary>
    public string PrimaryText => SplitIndex < 0 ? Label : Label[..SplitIndex];

    /// <summary>Remaining label segments (city, state, …) – the grey second line.</summary>
    public string? SecondaryText => SplitIndex < 0 ? null : Label[(SplitIndex + Separator.Length)..];

    private int SplitIndex => Label.IndexOf(Separator, StringComparison.Ordinal);

    /// <summary>
    /// Text and caret position for the "fill in" arrow of a suggestion (as in
    /// Google Maps): the search continues from the suggestion instead of
    /// selecting it, e.g. to add a house number.
    /// </summary>
    public (string Text, int Caret) GetFillIn()
    {
        // The caret goes right behind the first line, where a house number
        // belongs: "Nürtinger Straße |, Wendlingen" → "Nürtinger Straße 18, Wendlingen".
        // Same for POIs named like a street (the "Klosterstraße" U-Bahn station).
        string head = PrimaryText + " ";

        // Only the place follows: a POI's own street ("Parochialstraße") would
        // make Photon stick to the POI instead of the typed address.
        string? place = Place ?? SecondaryText;
        return place == null ? (head, head.Length) : ($"{head}, {place}", head.Length);
    }
}
