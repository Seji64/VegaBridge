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
    /// Icon identifier for the motorcycle display (manufacturer-specific, e.g. "turn-left", "roundabout-right-1").
    /// The plugin maps the Valhalla type to this format.
    /// </summary>
    public required string ManeuverIcon { get; init; }

    /// <summary>
    /// Instruction text for the display (e.g. "Turn right onto B31").
    /// </summary>
    public required string InstructionText { get; init; }

    /// <summary>
    /// Street name of the current/upcoming segment.
    /// </summary>
    public required string StreetName { get; init; }

    /// <summary>
    /// Intersection/street of the destination/maneuver; often identical to StreetName in official captures.
    /// </summary>
    public string? IntersectionName { get; init; }

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

    /// <summary>
    /// Whether the destination has been reached (last maneuver completed).
    /// </summary>
    public required bool IsFinal { get; init; }
}

/// <summary>
/// Input data for navigation start.
/// Sent once when navigation begins.
/// </summary>
public sealed record NavigationStartInput
{
    /// <summary>
    /// Preview of upcoming maneuvers (optional, for displays with a route overview).
    /// </summary>
    public IReadOnlyList<NavigationUpdateInput>? UpcomingManeuvers { get; init; }

    /// <summary>
    /// Total route distance in kilometers.
    /// </summary>
    public required double TotalDistanceKm { get; init; }

    /// <summary>
    /// Total route duration in minutes.
    /// </summary>
    public required double TotalTimeMin { get; init; }

    /// <summary>
    /// Start coordinates of the route (first route point), used by the
    /// plugin for the DEST frame. Null when the route has no geometry.
    /// </summary>
    public double? StartLatitude { get; init; }
    public double? StartLongitude { get; init; }
}

/// <summary>
/// Input data for an off-route warning.
/// </summary>
public sealed record OffRouteAlertInput
{
    /// <summary>
    /// Deviation from the route in meters.
    /// </summary>
    public required double DistanceMeters { get; init; }

    /// <summary>
    /// Current latitude position.
    /// </summary>
    public required double Latitude { get; init; }

    /// <summary>
    /// Current longitude position.
    /// </summary>
    public required double Longitude { get; init; }

    /// <summary>
    /// Timestamp of the detection (UTC).
    /// </summary>
    public required DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
}