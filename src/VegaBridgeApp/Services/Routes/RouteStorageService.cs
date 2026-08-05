using System.Text.Json;
using Serilog;
using VegaBridgeApp.Models.Routes;

namespace VegaBridgeApp.Services.Routes;

/// <summary>
/// Route persistence using individual JSON files in the app's data directory.
/// </summary>
public class RouteStorageService : IRouteStorageService
{
    private readonly string _storageDir;
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public RouteStorageService()
    {
        _storageDir = Path.Combine(FileSystem.AppDataDirectory, "routes");

        if (Directory.Exists(_storageDir)) return;
        Directory.CreateDirectory(_storageDir);
        Log.Debug("Created route storage directory: {Dir}", _storageDir);
    }

    public async Task<List<SavedRoute>> GetAllRoutesAsync()
    {
        List<SavedRoute> routes = new List<SavedRoute>();

        try
        {
            string[] files = Directory.GetFiles(_storageDir, "*.json");
            Log.Debug("Found {Count} route files in {Dir}", files.Length, _storageDir);

            foreach (string file in files)
            {
                SavedRoute? route = await LoadFromFileAsync(file);
                if (route != null) routes.Add(route);
            }

            Log.Information("Loaded {Count} saved routes", routes.Count);
            return routes.OrderByDescending(r => r.CreatedAt).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load routes from {Dir}", _storageDir);
            return [];
        }
    }

    public async Task<SavedRoute?> GetRouteByIdAsync(string id)
    {
        string filePath = Path.Combine(_storageDir, $"{id}.json");
        return await LoadFromFileAsync(filePath);
    }

    public async Task SaveRouteAsync(SavedRoute route)
    {
        string filePath = Path.Combine(_storageDir, $"{route.Id}.json");
        string tempPath = Path.Combine(_storageDir, $"{route.Id}.tmp");

        try
        {
            route.UpdatedAt = DateTime.UtcNow;

            // Atomic write: temp file first, then rename
            await using (FileStream stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, route, _jsonOptions);
            }

            File.Move(tempPath, filePath, overwrite: true);

            Log.Information(
                "Saved route {Id} — {Name} ({Km:F1} km, {Pts} pts, {Bytes} bytes)",
                route.Id, route.Name, route.DistanceKm,
                route.Waypoints?.Count ?? 0, new FileInfo(filePath).Length);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save route {Id} ({Name})", route.Id, route.Name);
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw new IOException($"Failed to save route {route.Id}", ex);
        }
    }

    public async Task DeleteRouteAsync(string id)
    {
        string filePath = Path.Combine(_storageDir, $"{id}.json");

        if (File.Exists(filePath))
        {
            await Task.Run(() => File.Delete(filePath));
            Log.Information("Deleted route {Id}", id);
        }
    }

    public Task<bool> RouteExistsAsync(string id)
    {
        return Task.FromResult(File.Exists(Path.Combine(_storageDir, $"{id}.json")));
    }

    private async Task<SavedRoute?> LoadFromFileAsync(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        try
        {
            await using FileStream stream = File.OpenRead(filePath);
            return await JsonSerializer.DeserializeAsync<SavedRoute>(stream, _jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to deserialize route file: {File}", Path.GetFileName(filePath));
            return null;
        }
    }
}
