namespace VegaBridgeApp.Models.Valhalla;

/// <summary>
/// Road types the rider wants to avoid (route options). Flags, so a
/// selection is a single value (Preferences, saved routes).
/// </summary>
[Flags]
public enum RoadAvoidance
{
    None = 0,
    Highways = 1,
    Tolls = 2,
    Ferries = 4
}
