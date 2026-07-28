using VegaBridgeApp.Models.Valhalla;

namespace VegaBridgeApp.Models.Routes;

/// <summary>
/// Represents a route saved for offline access or later use.
/// </summary>
public class SavedRoute
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Unnamed Route";
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// The encoded polyline6 string for map display.
    /// </summary>
    public string? Polyline6 { get; set; }

    /// <summary>
    /// Distance in kilometers.
    /// </summary>
    public double DistanceKm { get; set; }

    /// <summary>
    /// Travel time in minutes.
    /// </summary>
    public double TimeMinutes { get; set; }

    /// <summary>
    /// The waypoints used to generate the route.
    /// </summary>
    public List<Coordinate>? Waypoints { get; set; } = [];
}
