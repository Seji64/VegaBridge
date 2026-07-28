using VegaBridgeApp.Models.Routes;

namespace VegaBridgeApp.Services.Routes;

/// <summary>
/// Service for persisting saved routes as JSON files in the app's sandbox.
/// </summary>
public interface IRouteStorageService
{
    /// <summary>
    /// Retrieves all saved routes from storage.
    /// </summary>
    Task<List<SavedRoute>> GetAllRoutesAsync();

    /// <summary>
    /// Retrieves a specific route by its ID.
    /// </summary>
    Task<SavedRoute?> GetRouteByIdAsync(string id);

    /// <summary>
    /// Saves a route to storage. Overwrites if the route already exists.
    /// </summary>
    Task SaveRouteAsync(SavedRoute route);

    /// <summary>
    /// Deletes a route from storage.
    /// </summary>
    Task DeleteRouteAsync(string id);

    /// <summary>
    /// Checks if a route exists in storage.
    /// </summary>
    Task<bool> RouteExistsAsync(string id);
}
