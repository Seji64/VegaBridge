using VegaBridgeApp.Models.Navigation;
using VegaBridgeApp.Models.Valhalla;

namespace VegaBridgeApp.Services.Navigation;

/// <summary>
/// Sink for navigation events.
/// 
/// Instead of public .NET events, the <see cref="NavigationService"/>
/// reports all external state changes through this interface.
/// This keeps the call chain explicit: caller → interface → implementation.
/// 
/// Current implementations:
/// - <see cref="Components.Pages.Map"/> (UI)
/// - <see cref="Services.BLE.BleNavigationCoordinator"/> (BLE)
/// 
/// Additional sinks can be added when needed, e.g. logging/replay.
/// </summary>
public interface INavigationSink
{
    /// <summary>
    /// Navigation has started.
    /// </summary>
    Task OnStartAsync(NavigationStartInfo start);

    /// <summary>
    /// The current maneuver has changed.
    /// </summary>
    Task OnManeuverAsync(NavigationManeuverInfo maneuver);

    /// <summary>
    /// Periodic status update (speed, remaining distance, turn instruction).
    /// </summary>
    Task OnStatusAsync(NavigationStatus status);

    /// <summary>
    /// The rider has clearly left the route.
    /// </summary>
    Task OnOffRouteAsync(double latitude, double longitude, double distanceMeters);

    /// <summary>
    /// Navigation finished – destination reached.
    /// </summary>
    Task OnFinishAsync();

    /// <summary>
    /// Navigation cancelled – user stopped manually.
    /// </summary>
    Task OnCancelAsync();

    /// <summary>
    /// A new route has been calculated (e.g. by rerouting).
    /// </summary>
    Task OnRouteUpdatedAsync(RouteResponse response);
}
