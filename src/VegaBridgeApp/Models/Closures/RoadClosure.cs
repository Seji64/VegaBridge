namespace VegaBridgeApp.Models.Closures;

/// <summary>
/// Kind of road closure / restriction as tagged in OpenStreetMap.
/// </summary>
public enum ClosureKind
{
    /// <summary>highway=construction – road under construction.</summary>
    Construction,

    /// <summary>construction=* – temporary works on an otherwise normal road.</summary>
    Roadworks,

    /// <summary>access=no / motor_vehicle=no – closed for (motor) traffic.</summary>
    Access,

    /// <summary>barrier=gate/bollard/… – physical barrier.</summary>
    Barrier
}

/// <summary>
/// A single road closure / restriction found near the route.
/// </summary>
/// <param name="OsmId">OSM element id (way id) or provider-specific element id.</param>
/// <param name="Kind">Closure category.</param>
/// <param name="Name">Street/road name if tagged, otherwise null.</param>
/// <param name="Highway">highway tag value if present (e.g. "primary", "residential").</param>
/// <param name="Latitude">Approximate position (way center or route-projected point).</param>
/// <param name="Longitude">Approximate position.</param>
/// <param name="LastModified">When the element was last edited in OSM / validity end for provider data.</param>
public sealed record RoadClosure(
    long OsmId,
    ClosureKind Kind,
    string? Name,
    string? Highway,
    double Latitude,
    double Longitude,
    DateTimeOffset? LastModified)
{
    /// <summary>Source provider key ("overpass", "mobidata", …).</summary>
    public string Source { get; init; } = "overpass";
}
