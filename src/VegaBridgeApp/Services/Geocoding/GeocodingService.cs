using System.Globalization;
using Flurl.Http;
using Serilog;
using VegaBridgeApp.Models.Geocoding;

namespace VegaBridgeApp.Services.Geocoding;

/// <summary>
/// Photon (Komoot)‑basiertes Geocoding mit Autocomplete – nutzt Flurl.
/// </summary>
public class GeocodingService : IGeocodingService
{
    internal const string HttpClientName = "Photon";
    
    private readonly FlurlClient _flurlClient;

    public GeocodingService(IHttpClientFactory httpClientFactory)
    {
        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
        _flurlClient = new FlurlClient(httpClient);
    }

    public async Task<List<GeoResult>> SuggestAsync(string query, int limit = 5, double? lon = null, double? lat = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return [];

        try
        {
            string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

            var request = _flurlClient
                .Request("api")
                .SetQueryParams(new { q = query, limit, lang });
            if (lon.HasValue && lat.HasValue)
                request = request.SetQueryParams(new { lon = lon.Value, lat = lat.Value });

            PhotonResponse? response = await request.GetJsonAsync<PhotonResponse>(cancellationToken: ct);

            if (response?.Features == null || response.Features.Count == 0)
                return [];

            return response.Features
                .Select(MapToGeoResult)
                .Where(r => r is not null)
                // MudAutocomplete keys items by the GeoResult (ToString=Label) –
                // duplicate labels would crash the renderer.
                .DistinctBy(r => r!.Label)
                .ToList()!;
        }
        catch (FlurlHttpException ex)
        {
            Log.Warning(ex, "Photon request failed for query '{Query}'", query);
            return [];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Photon request failed unexpectedly for query '{Query}'", query);
            return [];
        }
    }

    public async Task<List<GeoResult>> GetReverseGeocodingAsync(double lon, double lat, CancellationToken ct = default)
    {
        try
        {
            string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

            PhotonResponse? response = await _flurlClient
                .Request("reverse")
                .SetQueryParams(new { lon, lat, lang })
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
            Log.Warning(ex, "Photon request failed");
            return [];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Photon request failed unexpectedly");
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

            // Empty label → blank dropdown row (and DistinctBy("") would collapse them).
            if (string.IsNullOrWhiteSpace(label)) return null;

            return new GeoResult(label, lat, lon, p.Type ?? p.OsmValue);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to map Photon feature");
            return null;
        }
    }

    private static string BuildLabel(PhotonProperties p)
    {
        List<string> parts = [];

        // POI-Name zuerst – matches the search term (e.g. "Kaufland").
        if (!string.IsNullOrWhiteSpace(p.Name)
            && !string.Equals(p.Name, p.Street, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(p.Name);
        }

        // Adresse mit Hausnummer
        if (!string.IsNullOrWhiteSpace(p.Street))
        {
            string address = p.Street;
            if (!string.IsNullOrWhiteSpace(p.Housenumber))
                address += " " + p.Housenumber;
            if (!string.Equals(address, p.Name, StringComparison.OrdinalIgnoreCase))
                parts.Add(address);
        }

        // Fallback: nur Name vorhanden (z.B. POI ohne Straße)
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(p.Name))
            parts.Add(p.Name);

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
