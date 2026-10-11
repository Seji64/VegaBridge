using VegaBridgeApp.Utils;
using Xunit;

namespace VegaBridgeApp.Tests;

public class GeoMathTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(200)]
    [InlineData(359)]
    public void Offset_LiesAtDistanceAndBearing(double bearing)
    {
        (double lat, double lon) = GeoMath.Offset(48.0, 9.0, bearing, 250);

        Assert.Equal(250, GeoMath.DistanceMeters(48.0, 9.0, lat, lon), 1);
        Assert.True(GeoMath.BearingDifferenceDeg(bearing, GeoMath.BearingDeg(48.0, 9.0, lat, lon)) < 0.1);
    }

    [Theory]
    [InlineData(10, 350, 20)] // across north
    [InlineData(350, 10, 20)]
    [InlineData(0, 180, 180)]
    [InlineData(90, 100, 10)]
    [InlineData(720, 5, 5)]
    public void BearingDifference_IsTheSmallerAngle(double a, double b, double expected)
    {
        Assert.Equal(expected, GeoMath.BearingDifferenceDeg(a, b), 6);
    }
}
