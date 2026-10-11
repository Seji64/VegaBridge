using VegaBridgeApp.Models.Geocoding;
using Xunit;

namespace VegaBridgeApp.Tests;

/// <summary>
/// Suggestion display (primary / secondary line) and the "fill in" arrow text
/// of the address autocomplete.
/// </summary>
public class GeoResultTests
{
    [Fact]
    public void Label_IsSplitAtFirstSeparator()
    {
        var poi = new GeoResult("Nürtinger Keller, Schloßgartenstraße, Nürtingen", 0, 0, "house");

        Assert.Equal("Nürtinger Keller", poi.PrimaryText);
        Assert.Equal("Schloßgartenstraße, Nürtingen", poi.SecondaryText);
    }

    [Fact]
    public void Label_WithoutSeparator_HasNoSecondaryLine()
    {
        var place = new GeoResult("Deutschland", 0, 0, "country");

        Assert.Equal("Deutschland", place.PrimaryText);
        Assert.Null(place.SecondaryText);
    }

    [Fact]
    public void FillIn_Street_PutsCaretBehindStreetName()
    {
        var street = new GeoResult("Nürtinger Straße, Wendlingen am Neckar, Baden-Württemberg", 0, 0, "street");

        (string text, int caret) = street.GetFillIn();

        Assert.Equal("Nürtinger Straße , Wendlingen am Neckar, Baden-Württemberg", text);
        Assert.Equal("Nürtinger Straße ".Length, caret);
        // Typing a house number at the caret yields a regular address.
        Assert.Equal("Nürtinger Straße 18, Wendlingen am Neckar, Baden-Württemberg", text.Insert(caret, "18"));
    }

    [Theory]
    [InlineData("Stuttgart, Baden-Württemberg, Deutschland", "city")]
    [InlineData("Nürtinger Straße 18, Wolfschlugen, Baden-Württemberg", "house")]
    [InlineData("Nürtinger Straße", "street")]
    public void FillIn_Other_AppendsSpaceAndPutsCaretAtEnd(string label, string type)
    {
        (string text, int caret) = new GeoResult(label, 0, 0, type).GetFillIn();

        Assert.Equal(label + " ", text);
        Assert.Equal(text.Length, caret);
    }
}
