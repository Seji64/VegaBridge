using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

/// <summary>
/// Valhalla costing options – only the "avoid" subset the app uses.
/// Unset (null) values keep Valhalla's defaults.
/// </summary>
/// <remarks>
/// Every avoided road type is sent twice:
/// <list type="bullet">
/// <item>soft <c>use_*</c> = 0 – always honoured, but only a preference
/// (Lyon → Marseille with use_tolls=0 still took 31 km of tolled A7);</item>
/// <item>hard <c>exclude_*</c> – reliable, but needs
/// <c>service_limits.allow_hard_exclusions</c> on the server (ignored with a
/// warning otherwise) and can leave no path at all (island only reachable by
/// ferry). <c>ValhallaClient</c> then retries with the soft ones only.</item>
/// </list>
/// </remarks>
public class CostingOptions
{
    [JsonPropertyName("use_highways")]
    public double? UseHighways { get; set; }

    [JsonPropertyName("use_tolls")]
    public double? UseTolls { get; set; }

    [JsonPropertyName("use_ferry")]
    public double? UseFerry { get; set; }

    [JsonPropertyName("exclude_highways")]
    public bool? ExcludeHighways { get; set; }

    [JsonPropertyName("exclude_tolls")]
    public bool? ExcludeTolls { get; set; }

    [JsonPropertyName("exclude_ferries")]
    public bool? ExcludeFerries { get; set; }

    [JsonIgnore]
    public bool HasHardExclusions => ExcludeHighways == true || ExcludeTolls == true || ExcludeFerries == true;

    /// <summary>Drops the hard exclusions, keeps the soft preferences.</summary>
    public void RemoveHardExclusions() => ExcludeHighways = ExcludeTolls = ExcludeFerries = null;

    /// <summary>
    /// <c>costing_options</c> for <paramref name="costing"/> that avoid the
    /// given road types; null (Valhalla defaults) when nothing is avoided.
    /// </summary>
    public static Dictionary<string, CostingOptions>? Avoiding(RoadAvoidance avoid, string costing = "motorcycle")
    {
        if (avoid == RoadAvoidance.None) return null;

        bool highways = avoid.HasFlag(RoadAvoidance.Highways);
        bool tolls = avoid.HasFlag(RoadAvoidance.Tolls);
        bool ferries = avoid.HasFlag(RoadAvoidance.Ferries);
        return new()
        {
            [costing] = new CostingOptions
            {
                UseHighways = highways ? 0 : null,
                UseTolls = tolls ? 0 : null,
                UseFerry = ferries ? 0 : null,
                ExcludeHighways = highways ? true : null,
                ExcludeTolls = tolls ? true : null,
                ExcludeFerries = ferries ? true : null
            }
        };
    }
}
