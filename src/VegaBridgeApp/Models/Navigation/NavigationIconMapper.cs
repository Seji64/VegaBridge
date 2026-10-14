namespace VegaBridgeApp.Models.Navigation;

/// <summary>
/// Single Source of Truth for mapping Valhalla maneuver types to semantic icon keys.
/// This mapper is plugin-agnostic and serves as the bridge between the 
/// navigation domain and both the UI and hardware plugins.
/// The keys keep what Valhalla distinguishes (keep vs. slight, ramp vs.
/// exit, waypoint vs. destination); each plugin folds them into the icons
/// its display knows.
/// </summary>
public static class NavigationIconMapper
{
    // Semantic keys used by both UI and BLE plugins
    public const string IconStraight = "straight";
    public const string IconTurnLeft = "turn-left";
    public const string IconTurnRight = "turn-right";
    public const string IconSlightLeft = "turn-slight-left";
    public const string IconSlightRight = "turn-slight-right";
    public const string IconSharpLeft = "turn-sharp-left";
    public const string IconSharpRight = "turn-sharp-right";
    public const string IconUTurnLeft = "uturn-left";
    public const string IconUTurnRight = "uturn-right";
    // Keep left/right at a fork.
    public const string IconKeepLeft = "keep-left";
    public const string IconKeepRight = "keep-right";
    // Ramps onto a motorway/highway.
    public const string IconRampLeft = "ramp-left";
    public const string IconRampRight = "ramp-right";
    // Motorway/highway exits (e.g. "take the B10 exit") – shown as
    // an exit/ramp icon in the UI instead of a plain left/right turn.
    public const string IconExitLeft = "exit-left";
    public const string IconExitRight = "exit-right";
    public const string IconMergeLeft = "merge-left";
    public const string IconMergeRight = "merge-right";
    public const string IconRoundabout = "roundabout";
    public const string IconFerry = "ferry";
    // Arrival at an intermediate waypoint – only the very last arrival of
    // the route is the destination (IconFinish).
    public const string IconWaypoint = "waypoint";
    public const string IconFinish = "finish";

    private static readonly Dictionary<int, string> ValhallaToSemanticIcon = new()
    {
        { 0, IconStraight },        // kNone
        { 1, IconStraight },        // kStart
        { 2, IconTurnRight },       // kStartRight
        { 3, IconTurnLeft },        // kStartLeft
        { 4, IconFinish },           // kDestination
        { 5, IconFinish },          // kDestinationRight
        { 6, IconFinish },          // kDestinationLeft
        { 7, IconStraight },        // kBecomes
        { 8, IconStraight },        // kContinue
        { 9, IconSlightRight },      // kSlightRight
        { 10, IconTurnRight },      // kRight
        { 11, IconSharpRight },     // kSharpRight
        { 12, IconUTurnRight },      // kUturnRight
        { 13, IconUTurnLeft },       // kUturnLeft
        { 14, IconSharpLeft },       // kSharpLeft
        { 15, IconTurnLeft },       // kLeft
        { 16, IconSlightLeft },     // kSlightLeft
        { 17, IconStraight },        // kRampStraight
        { 18, IconRampRight },      // kRampRight
        { 19, IconRampLeft },        // kRampLeft
        { 20, IconExitRight },      // kExitRight
        { 21, IconExitLeft },        // kExitLeft
        { 22, IconStraight },        // kStayStraight
        { 23, IconKeepRight },       // kStayRight
        { 24, IconKeepLeft },       // kStayLeft
        { 25, IconStraight },        // kMerge
        { 26, IconRoundabout },      // kRoundaboutEnter
        { 27, IconRoundabout },      // kRoundaboutExit
        { 28, IconFerry },           // kFerryEnter
        { 29, IconFerry },           // kFerryExit
        { 30, IconStraight },        // kTransit
        { 31, IconStraight },        // kTransitTransfer
        { 32, IconStraight },        // kTransitRemainOn
        { 33, IconStraight },        // kTransitConnectionStart
        { 34, IconStraight },        // kTransitConnectionTransfer
        { 35, IconStraight },        // kTransitConnectionDestination
        { 36, IconStraight },        // kPostTransitConnectionDestination
        { 37, IconMergeRight },      // kMergeRight
        { 38, IconMergeLeft },       // kMergeLeft
        { 39, IconFinish },          // kElevatorEnter
        { 40, IconFinish },          // kStepsEnter
        { 41, IconFinish },          // kEscalatorEnter
        { 42, IconFinish },          // kBuildingEnter
        { 43, IconFinish }           // kBuildingExit
    };

    /// <summary>
    /// Resolves a Valhalla maneuver type to a semantic icon key.
    /// </summary>
    /// <param name="valhallaType">Valhalla maneuver type.</param>
    /// <param name="isFinalManeuver">false for every maneuver but the route's
    /// last: Valhalla emits an arrival (kDestination*) at the end of EVERY
    /// leg, and only the last one is the destination – the others are
    /// waypoints.</param>
    public static string GetSemanticIcon(int valhallaType, bool isFinalManeuver = true)
    {
        if (!isFinalManeuver && valhallaType is 4 or 5 or 6)
            return IconWaypoint;

        return ValhallaToSemanticIcon.TryGetValue(valhallaType, out var icon) 
            ? icon 
            : IconStraight;
    }

    /// <summary>
    /// Resolves a maneuver of the running route to a semantic icon key
    /// (waypoint arrivals included).
    /// </summary>
    public static string GetSemanticIcon(NavigationManeuverInfo maneuver) =>
        GetSemanticIcon(maneuver.ValhallaType, maneuver.Index >= maneuver.Total - 1);
}
