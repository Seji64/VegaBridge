namespace VegaBridgeApp.Models.BLE;

/// <summary>
/// Shared kernel models for communication between the navigation domain and BLE plugins.
/// These models intentionally live in the Models.BLE namespace to avoid circular dependencies:
/// - NavigationService does NOT reference Models.BLE
/// - Plugins reference Models.BLE (input)
/// - Coordinator maps NavigationService models -> Models.BLE
/// </summary>

/// <summary>
/// Input data for a navigation update sent to the motorcycle display.
/// Sent periodically (throttled) and on maneuver changes.
/// </summary>
public sealed record NavigationUpdateInput
{
    /// <summary>
    /// Plugin-agnostic semantic icon (NavigationIconMapper.Icon*); the plugin
    /// maps it to the keys its display knows.
    /// </summary>
    public required string ManeuverIcon { get; init; }

    /// <summary>
    /// Number of the roundabout exit to take (Valhalla roundabout_exit_count,
    /// set on the roundabout-enter maneuver), null otherwise.
    /// </summary>
    public int? RoundaboutExitCount { get; init; }

    /// <summary>
    /// Instruction text for the display (e.g. "Turn right onto B31").
    /// </summary>
    public required string InstructionText { get; init; }

    /// <summary>
    /// Street name of the current/upcoming segment.
    /// </summary>
    public required string StreetName { get; init; }

    /// <summary>
    /// Distance to the next maneuver in meters.
    /// </summary>
    public required double DistanceToTurnM { get; init; }

    /// <summary>
    /// Current speed in km/h.
    /// </summary>
    public required double SpeedKmh { get; init; }

    /// <summary>
    /// Remaining total distance in kilometers.
    /// </summary>
    public required double RemainingDistanceKm { get; init; }

    /// <summary>
    /// Remaining total time in minutes.
    /// </summary>
    public required double RemainingTimeMin { get; init; }

    /// <summary>
    /// Index of the current maneuver (0-based).
    /// </summary>
    public required int CurrentManeuverIndex { get; init; }

    /// <summary>
    /// Total number of maneuvers in the route.
    /// </summary>
    public required int TotalManeuvers { get; init; }
}

/// <summary>
/// Input data for the navigation start frames. Sent when navigation begins,
/// after a reroute and after a (re)connect mid-session.
/// </summary>
public sealed record NavigationStartInput
{
    /// <summary>
    /// Remaining route distance in kilometers (total at the start, current
    /// remainder on a re-send after reroute or reconnect).
    /// </summary>
    public required double TotalDistanceKm { get; init; }

    /// <summary>
    /// Final destination of the route, used by the plugin for the DEST frame.
    /// Null for test sequences without a real route.
    /// </summary>
    public double? DestinationLatitude { get; init; }
    public double? DestinationLongitude { get; init; }
}
