namespace VegaBridgeApp.Models.Navigation;

/// <summary>
/// Shared navigation constants and mappings that are independent of any specific BLE plugin.
/// This ensures UI and BLE layers can stay in sync without direct coupling.
/// </summary>
public static class NavigationConstants
{
    // Valhalla maneuver types that are considered "straight" for look-ahead purposes
    private static readonly HashSet<int> StraightManeuverTypes =
    [
        1,  // kStart
        7,  // kBecomes
        8,  // kContinue
        17, // kRampStraight
        22, // kStayStraight
        25  // kMerge
    ];

    /// <summary>
    /// Determines if a Valhalla maneuver type represents a straight segment.
    /// Used by both UI and BLE layers for consistent look-ahead behavior.
    /// </summary>
    public static bool IsStraightManeuver(int valhallaType)
    {
        return StraightManeuverTypes.Contains(valhallaType);
    }
}