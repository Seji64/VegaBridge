using VegaBridgeApp.Models.Geocoding;

namespace VegaBridgeApp.Services.Geocoding;

/// <summary>
/// Location search and autocomplete service based on Photon (Komoot).
/// </summary>
public interface IGeocodingService
{
    /// <summary>
    /// Suggests locations matching <paramref name="query"/> (autocomplete).
    /// Leeres oder zu kurzes Query → leere Liste.
    /// </summary>
    Task<List<GeoResult>> SuggestAsync(string query, int limit = 5, CancellationToken ct = default);
    
    Task<List<GeoResult>> GetReverseGeocodingAsync(double lon, double lat, CancellationToken ct = default);
}
