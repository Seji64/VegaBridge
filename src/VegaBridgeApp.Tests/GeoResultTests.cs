using VegaBridgeApp.Models.Geocoding;
using VegaBridgeApp.Services.Geocoding;
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
        GeoResult street = Map(new PhotonProperties
        {
            Name = "Nürtinger Straße", City = "Wendlingen am Neckar", State = "Baden-Württemberg", Type = "street"
        });

        (string text, int caret) = street.GetFillIn();

        Assert.Equal("Nürtinger Straße , Wendlingen am Neckar, Baden-Württemberg", text);
        Assert.Equal("Nürtinger Straße 18, Wendlingen am Neckar, Baden-Württemberg", text.Insert(caret, "18"));
    }

    [Fact]
    public void FillIn_PoiNamedLikeStreet_DropsPoiStreet()
    {
        // U-Bahn station "Klosterstraße" in the Parochialstraße.
        GeoResult station = Map(new PhotonProperties
        {
            Name = "Klosterstraße", Street = "Parochialstraße", City = "Berlin", State = "Berlin", Type = "house"
        });
        Assert.Equal("Klosterstraße, Parochialstraße, Berlin", station.Label);

        (string text, int caret) = station.GetFillIn();

        Assert.Equal("Klosterstraße 5, Berlin", text.Insert(caret, "5"));
    }

    [Fact]
    public void FillIn_House_PutsCaretBehindHouseNumber()
    {
        GeoResult house = Map(new PhotonProperties
        {
            Street = "Nürtinger Straße", Housenumber = "18", City = "Wolfschlugen", State = "Baden-Württemberg", Type = "house"
        });

        (string text, int caret) = house.GetFillIn();

        Assert.Equal("Nürtinger Straße 18, Wolfschlugen, Baden-Württemberg", text);
        Assert.Equal("Nürtinger Straße 18".Length, caret);
    }

    [Fact]
    public void FillIn_City_PutsCaretBehindCity()
    {
        GeoResult city = Map(new PhotonProperties
        {
            Name = "Stuttgart", City = "Stuttgart", State = "Baden-Württemberg", Country = "Deutschland", Type = "city"
        });
        Assert.Equal("Stuttgart, Baden-Württemberg, Deutschland", city.Label);

        (string text, int caret) = city.GetFillIn();

        Assert.Equal("Stuttgart , Baden-Württemberg, Deutschland", text);
        Assert.Equal("Stuttgart ".Length, caret);
    }

    [Fact]
    public void FillIn_WithoutPlace_AppendsSpace()
    {
        (string text, int caret) = new GeoResult("Deutschland", 0, 0, "country").GetFillIn();

        Assert.Equal("Deutschland ", text);
        Assert.Equal(text.Length, caret);
    }

    private static GeoResult Map(PhotonProperties properties) =>
        GeocodingService.MapToGeoResult(new PhotonFeature
        {
            Geometry = new PhotonGeometry { Coordinates = [9.0, 48.0] },
            Properties = properties
        })!;
}
