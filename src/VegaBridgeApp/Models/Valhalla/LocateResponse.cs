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

public class LocateEdge
{
    [JsonPropertyName("edge_info")]
    public LocateEdgeInfo? EdgeInfo { get; set; }

    [JsonPropertyName("percent_along")]
    public double PercentAlong { get; set; }

    [JsonPropertyName("side_of_street")]
    public string? SideOfStreet { get; set; }
}

public class LocateEdgeInfo
{
    [JsonPropertyName("way_id")]
    public long WayId { get; set; }

    [JsonPropertyName("names")]
    public List<string>? Names { get; set; }
}
