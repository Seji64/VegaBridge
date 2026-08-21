using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

/// <summary>
/// Response from Valhalla's /locate endpoint.
/// Returns edge information (way_id, road name) for GPS points.
/// </summary>
public class LocateResponse
{
    [JsonPropertyName("edges")]
    public List<LocateEdge>? Edges { get; set; }
}

/// <summary>
/// One edge from a /locate response. The public Valhalla API returns
/// <c>way_id</c> at the TOP LEVEL of each edge object (there is no
/// <c>edge_info</c> nesting) – model it flat or every way_id is lost
/// (way-id index silently ends up with 0 ways, see nav-log 2026-08-21).
/// </summary>
public class LocateEdge
{
    [JsonPropertyName("way_id")]
    public long WayId { get; set; }

    [JsonPropertyName("percent_along")]
    public double PercentAlong { get; set; }

    [JsonPropertyName("side_of_street")]
    public string? SideOfStreet { get; set; }
}
