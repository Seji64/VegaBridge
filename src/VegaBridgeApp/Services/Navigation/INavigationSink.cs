using VegaBridgeApp.Models.Navigation;
using VegaBridgeApp.Models.Valhalla;

namespace VegaBridgeApp.Services.Navigation;

/// <summary>
/// Sink für Navigation-Events.
/// 
/// Statt öffentlicher .NET-Events kann der <see cref="NavigationService"/>
/// alle externen Zustandsänderungen über diese Schnittstelle melden.
/// Das macht die Aufrufkette explizit: Aufrufer → Interface → Implementierung.
/// 
/// Aktuelle Implementierungen:
/// - <see cref="Components.Pages.Map"/> (UI)
/// - <see cref="Services.BLE.BleNavigationCoordinator"/> (BLE)
/// 
/// Bei Bedarf können weitere Sinks hinzukommen, z.B. Logging/Replay.
/// </summary>
public interface INavigationSink
{
    /// <summary>
    /// Navigation wurde gestartet.
    /// </summary>
    Task OnStartAsync(NavigationStartInfo start);

    /// <summary>
    /// Aktuelles Manöver hat sich geändert.
    /// </summary>
    Task OnManeuverAsync(NavigationManeuverInfo maneuver);

    /// <summary>
    /// Periodisches Status-Update (Geschwindigkeit, Reststrecke, Abbiegehinweis).
    /// </summary>
    Task OnStatusAsync(NavigationStatus status);

    /// <summary>
    /// Fahrer ist deutlich von der Route abgekommen.
    /// </summary>
    Task OnOffRouteAsync(double latitude, double longitude, double distanceMeters);

    /// <summary>
    /// Navigation beendet – Ziel erreicht.
    /// </summary>
    Task OnFinishAsync();

    /// <summary>
    /// Navigation abgebrochen – Benutzer hat manuell gestoppt.
    /// </summary>
    Task OnCancelAsync();

    /// <summary>
    /// Eine neue Route wurde berechnet (z.B. durch Rerouting).
    /// </summary>
    Task OnRouteUpdatedAsync(RouteResponse response);
}
