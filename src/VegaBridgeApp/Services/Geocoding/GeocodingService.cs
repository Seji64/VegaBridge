using Flurl.Http;
using Microsoft.Extensions.Logging;
using VegaBridgeApp.Models.Geocoding;

namespace VegaBridgeApp.Services.Geocoding;

/// <summary>
/// Photon (Komoot)‑basiertes Geocoding mit Autocomplete – nutzt Flurl.
/// </summary>
public class GeocodingService : IGeocodingService
{
    internal const string HttpClientName = "Photon";
    
    private readonly FlurlClient _flurlClient;
    private readonly ILogger<GeocodingService> _logger;

    public GeocodingService(IHttpClientFactory httpClientFactory, ILogger<GeocodingService> logger)
    {
        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
        _flurlClient = new FlurlClient(httpClient);
        _logger = logger;
    }

    public async Task<List<GeoResult>> SuggestAsync(string query, int limit = 5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return [];

        try
        {
            
            PhotonResponse? response = await _flurlClient
                .Request("api")
                .SetQueryParams(new { q = query, limit, lang = "de" })
                .GetJsonAsync<PhotonResponse>(cancellationToken: ct);

            if (response?.Features == null || response.Features.Count == 0)
                return [];

            return response.Features
                .Select(MapToGeoResult)
                .Where(r => r is not null)
                .ToList()!;
        }
        catch (FlurlHttpException ex)
        {
            _logger.LogWarning(ex, "Photon request failed for query '{Query}'", query);
            return [];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }

    public async Task<List<GeoResult>> GetReverseGeocodingAsync(double lon, double lat, CancellationToken ct = default)
    {
        try
        {
            PhotonResponse? response = await _flurlClient
                .Request("reverse")
                .SetQueryParams(new { lon = lon, lat = lat, lang = "de" })
                .GetJsonAsync<PhotonResponse>(cancellationToken: ct);
            
            if (response?.Features == null || response.Features.Count == 0)
                return [];

            return response.Features
                .Select(MapToGeoResult)
                .Where(r => r is not null)
                .ToList()!;
        }
        catch (FlurlHttpException ex)
        {
            _logger.LogWarning(ex, "Photon request failed");
            return [];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }

    private static GeoResult? MapToGeoResult(PhotonFeature feature)
    {
        try
        {
            PhotonProperties p = feature.Properties;
            List<double> coords = feature.Geometry.Coordinates;

            if (coords.Count < 2) return null;

            double lon = coords[0];
            double lat = coords[1];
            string label = BuildLabel(p);

            return new GeoResult(label, lat, lon, p.Type ?? p.OsmValue);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildLabel(PhotonProperties p)
    {
        List<string> parts = [];

        // Adresse mit Hausnummer: "Altenbergweg 18"
        if (!string.IsNullOrWhiteSpace(p.Street))
        {
            string address = p.Street;
            if (!string.IsNullOrWhiteSpace(p.Housenumber))
                address += " " + p.Housenumber;
            parts.Add(address);
        }
        else if (!string.IsNullOrWhiteSpace(p.Name))
        {
            parts.Add(p.Name);
        }

        // Stadt (falls nicht identisch mit Name/Straße)
        string? lastPart = parts.LastOrDefault();
        if (!string.IsNullOrWhiteSpace(p.City)
            && !string.Equals(p.City, lastPart, StringComparison.OrdinalIgnoreCase))
            parts.Add(p.City);

        // Bundesland (falls abweichend von Stadt)
        lastPart = parts.LastOrDefault();
        if (!string.IsNullOrWhiteSpace(p.State)
            && !string.Equals(p.State, lastPart, StringComparison.OrdinalIgnoreCase))
            parts.Add(p.State);

        // Land (nur wenn wenig Kontext)
        if (!string.IsNullOrWhiteSpace(p.Country) && parts.Count < 3)
            parts.Add(p.Country);

        return string.Join(", ", parts);
    }
}
