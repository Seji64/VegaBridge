namespace VegaBridgeApp.Models.BLE;

/// <summary>
/// Shared Kernel Models für die Kommunikation zwischen Navigation-Domain und BLE-Plugins.
/// Diese Models sind bewusst im Namespace Models.BLE angesiedelt, um Zirkelabhängigkeiten zu vermeiden:
/// - NavigationService referenziert NICHT Models.BLE
/// - Plugins referenzieren Models.BLE (Input)
/// - Coordinator mapped NavigationService Models -> Models.BLE
/// </summary>

/// <summary>
/// Eingabedaten für ein Navigations-Update an das Motorrad-Display.
/// Wird periodisch (Throttled) und bei Manöverwechseln gesendet.
/// </summary>
public sealed record NavigationUpdateInput
{
    /// <summary>
    /// Icon-Identifier für das Motorrad-Display (herstellerspezifisch, z.B. "turn-left", "roundabout-right-1").
    /// Das Plugin mappt den Valhalla-Type auf dieses Format.
    /// </summary>
    public required string ManeuverIcon { get; init; }

    /// <summary>
    /// Anweisungstext für die Anzeige (z.B. "Rechts abbiegen auf B31").
    /// </summary>
    public required string InstructionText { get; init; }

    /// <summary>
    /// Straßenname des aktuellen/kommenden Segments.
    /// </summary>
    public required string StreetName { get; init; }

    /// <summary>
    /// Kreuzung/Straße des Ziels/manövers; often identical to StreetName in official captures.
    /// </summary>
    public string? IntersectionName { get; init; }

    /// <summary>
    /// Distanz zum nächsten Manöver in Metern.
    /// </summary>
    public required double DistanceToTurnM { get; init; }

    /// <summary>
    /// Aktuelle Geschwindigkeit in km/h.
    /// </summary>
    public required double SpeedKmh { get; init; }

    /// <summary>
    /// Verbleibende Gesamtstrecke in Kilometern.
    /// </summary>
    public required double RemainingDistanceKm { get; init; }

    /// <summary>
    /// Verbleibende Gesamtzeit in Minuten.
    /// </summary>
    public required double RemainingTimeMin { get; init; }

    /// <summary>
    /// Index des aktuellen Manövers (0-basiert).
    /// </summary>
    public required int CurrentManeuverIndex { get; init; }

    /// <summary>
    /// Gesamtzahl der Manöver in der Route.
    /// </summary>
    public required int TotalManeuvers { get; init; }

    /// <summary>
    /// Ob das Ziel erreicht ist (letztes Manöver abgeschlossen).
    /// </summary>
    public required bool IsFinal { get; init; }
}

/// <summary>
/// Eingabedaten für den Navigations-Start.
/// Wird einmalig beim Start der Navigation gesendet.
/// </summary>
public sealed record NavigationStartInput
{
    /// <summary>
    /// Vorschau der kommenden Manöver (optional, für Displays mit Routenübersicht).
    /// </summary>
    public IReadOnlyList<NavigationUpdateInput>? UpcomingManeuvers { get; init; }

    /// <summary>
    /// Gesamtstrecke der Route in Kilometern.
    /// </summary>
    public required double TotalDistanceKm { get; init; }

    /// <summary>
    /// Gesamtdauer der Route in Minuten.
    /// </summary>
    public required double TotalTimeMin { get; init; }
}

/// <summary>
/// Eingabedaten für eine Off-Route Warnung.
/// </summary>
public sealed record OffRouteAlertInput
{
    /// <summary>
    /// Abweichung von der Route in Metern.
    /// </summary>
    public required double DistanceMeters { get; init; }

    /// <summary>
    /// Aktuelle Breitengrad-Position.
    /// </summary>
    public required double Latitude { get; init; }

    /// <summary>
    /// Aktuelle Längengrad-Position.
    /// </summary>
    public required double Longitude { get; init; }

    /// <summary>
    /// Timestamp der Erkennung (UTC).
    /// </summary>
    public required DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
}