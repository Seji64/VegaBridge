namespace VegaBridgeApp.Models.Navigation;

/// <summary>
/// Single Source of Truth for mapping Valhalla maneuver types to semantic icon keys.
/// This mapper is plugin-agnostic and serves as the bridge between the 
/// navigation domain and both the UI and hardware plugins.
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
    public const string IconUTurn = "uturn";
    public const string IconRoundabout = "roundabout";
    public const string IconFinish = "finish";

    private static readonly Dictionary<int, string> ValhallaToSemanticIcon = new()
    {
        { 0, IconStraight },        // kNone
        { 1, IconStraight },        // kStart
        { 2, IconTurnRight },       // kStartRight
        { 3, IconTurnLeft },        // kStartLeft
        { 4, IconFinish },           // kDestination
        { 5, IconTurnRight },       // kDestinationRight
        { 6, IconTurnLeft },        // kDestinationLeft
        { 7, IconStraight },        // kBecomes
        { 8, IconStraight },        // kContinue
        { 9, IconSlightRight },      // kSlightRight
        { 10, IconTurnRight },      // kRight
        { 11, IconSharpRight },     // kSharpRight
        { 12, IconUTurn },           // kUturnRight
        { 13, IconUTurn },           // kUturnLeft
        { 14, IconSharpLeft },       // kSharpLeft
        { 15, IconTurnLeft },       // kLeft
        { 16, IconSlightLeft },     // kSlightLeft
        { 17, IconStraight },        // kRampStraight
        { 18, IconTurnRight },      // kRampRight
        { 19, IconTurnLeft },        // kRampLeft
        { 20, IconTurnRight },      // kExitRight
        { 21, IconTurnLeft },        // kExitLeft
        { 22, IconStraight },        // kStayStraight
        { 23, IconSlightRight },     // kStayRight
        { 24, IconSlightLeft },     // kStayLeft
        { 25, IconStraight },        // kMerge
        { 26, IconRoundabout },      // kRoundaboutEnter
        { 27, IconRoundabout },      // kRoundaboutExit
        { 28, IconStraight },        // kFerryEnter
        { 29, IconStraight },        // kFerryExit
        { 30, IconStraight },        // kTransit
        { 31, IconStraight },        // kTransitTransfer
        { 32, IconStraight },        // kTransitRemainOn
        { 33, IconStraight },        // kTransitConnectionStart
        { 34, IconStraight },        // kTransitConnectionTransfer
        { 35, IconStraight },        // kTransitConnectionDestination
        { 36, IconStraight },        // kPostTransitConnectionDestination
        { 37, IconSlightRight },     // kMergeRight
        { 38, IconSlightLeft },      // kMergeLeft
        { 39, IconFinish },          // kElevatorEnter
        { 40, IconFinish },          // kStepsEnter
        { 41, IconFinish },          // kEscalatorEnter
        { 42, IconFinish },          // kBuildingEnter
        { 43, IconFinish }           // kBuildingExit
    };

    /// <summary>
    /// Resolves a Valhalla maneuver type to a semantic icon key.
    /// </summary>
    public static string GetSemanticIcon(int valhallaType)
    {
        return ValhallaToSemanticIcon.TryGetValue(valhallaType, out var icon) 
            ? icon 
            : IconStraight;
    }
}
