using VegaBridgeApp.Utils;
using Xunit;

namespace VegaBridgeApp.Tests;

/// <summary>Rounding of the distance to the next turn and of durations in the UI.</summary>
public class DisplayRoundingTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(23, 20)]
    [InlineData(25, 30)]
    [InlineData(96, 100)]
    [InlineData(124, 100)]
    [InlineData(126, 150)]
    [InlineData(960, 950)]
    public void TurnDistance_BelowOneKm_InMeterSteps(double meters, double expected)
    {
        (double value, bool isKm) = DisplayRounding.TurnDistance(meters);

        Assert.False(isKm);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(980, 1.0)] // rounds up to 1000 m → shown as km, not "1000 m"
    [InlineData(1249, 1.2)]
    [InlineData(1250, 1.3)]
    [InlineData(12_345, 12.3)]
    public void TurnDistance_FromOneKm_InKmWithOneDecimal(double meters, double expected)
    {
        (double value, bool isKm) = DisplayRounding.TurnDistance(meters);

        Assert.True(isKm);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(0.4, 0, 0)]
    [InlineData(42.4, 0, 42)]
    [InlineData(59.6, 1, 0)] // not "59 Min" (truncated) or "60 Min"
    [InlineData(404.4, 6, 44)]
    public void Duration_RoundsToWholeMinutes(double minutes, int hours, int mins)
    {
        Assert.Equal((hours, mins), DisplayRounding.Duration(minutes));
    }
}
