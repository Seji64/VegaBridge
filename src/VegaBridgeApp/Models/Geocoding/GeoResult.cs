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
        // Streets: leave the caret right behind the street name, so a typed
        // house number lands where it belongs (Photon resolves both orders):
        // "Nürtinger Straße |, Wendlingen" → "Nürtinger Straße 18, Wendlingen".
        if (Type == "street" && SplitIndex >= 0)
            return (Label.Insert(SplitIndex, " "), SplitIndex + 1);

        string text = Label + " ";
        return (text, text.Length);
    }
}
