using VegaBridgeApp.Models.Geocoding;

namespace VegaBridgeApp.Models.Utils;

public class WaypointViewModel
{
    public Guid Id { get; } = Guid.NewGuid();
    public GeoResult? Location { get; set; }
    public int Order { get; set; }
}