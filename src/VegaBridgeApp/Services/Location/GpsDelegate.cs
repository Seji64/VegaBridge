using Microsoft.Extensions.Logging;
using Shiny.Locations;

namespace VegaBridgeApp.Services.Location;

/// <summary>
/// Background GPS delegate – receives position readings while the app is
/// backgrounded. Forwards readings to <see cref="GpsService"/> via the
/// static <see cref="ReadingReceived"/> event so the foreground-UI singleton can
/// pick them up without tight coupling.
/// </summary>
public class GpsDelegate(ILogger<GpsDelegate> logger) : IGpsDelegate
{
    /// <summary>
    /// Fired on the background delegate thread whenever a new GPS reading arrives.
    /// <see cref="GpsService"/> subscribes to this in its constructor.
    /// </summary>
    public static event Action<GpsReading>? ReadingReceived;

    /// <inheritdoc />
    public Task OnReading(GpsReading reading)
    {
        logger.LogDebug("Background GPS: {Lat:F5}, {Lon:F5} ±{Acc:F0}m",
            reading.Position.Latitude,
            reading.Position.Longitude,
            reading.PositionAccuracy);

        ReadingReceived?.Invoke(reading);
        return Task.CompletedTask;
    }
}
