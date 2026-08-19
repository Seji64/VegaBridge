using Flurl.Http;
using Serilog;
using VegaBridgeApp.Models.Valhalla;

namespace VegaBridgeApp.Services.Valhalla;

/// <summary>
/// Valhalla routing client.
/// Uses a named <c>HttpClient</c> (registered via <c>AddHttpClient</c> + <c>AddResilienceHandler</c>)
/// so retry, timeout, and circuit‑breaker are handled automatically at the transport layer.
/// Flurl is only used for the convenient JSON request/response syntax.
/// </summary>
public class ValhallaClient : IValhallaClient
{
    private readonly FlurlClient _flurlClient;

    public ValhallaClient(IHttpClientFactory httpClientFactory)
    {
        HttpClient httpClient = httpClientFactory.CreateClient(ValhallaOptions.HttpClientName);
        _flurlClient = new FlurlClient(httpClient);
    }

    /// <inheritdoc />
    public Task<Result> GetRouteAsync(RouteRequest request, CancellationToken cancellationToken = default) =>
        PostAsync("route", request, cancellationToken);

    /// <inheritdoc />
    public async Task<Result> GetMapMatchAsync(TraceRequest request, CancellationToken cancellationToken = default)
    {
        // shape_match is a top-level trace_route parameter; setting it under
        // costing_options would be silently ignored by Valhalla.
        request.ShapeMatch = "map_snap";
        return await PostAsync("trace_route", request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<LocateResponse?>> LocateAsync(List<(double Lat, double Lon)> points, double speedMs = 0, double heading = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Log.Debug("Locating {Count} points (speed={Speed:F1}m/s, heading={Heading:F0}°)", points.Count, speedMs, heading);
        try
        {
            // Dynamic search radius based on speed:
            // < 15 km/h (city/traffic lights): 15m – prevents snapping to cross-streets
            // 15-60 km/h (city riding): 35m
            // > 60 km/h (highway/country): 60m – absorbs high-speed GPS drift
            double speedKmh = speedMs * 3.6;
            int radius = speedKmh switch
            {
                < 15 => 15,
                < 60 => 35,
                _ => 60
            };

            var request = new
            {
                locations = points.Select(p => new
                {
                    lat = p.Lat,
                    lon = p.Lon,
                    radius,
                    heading = heading > 0 ? heading : (double?)null,
                    heading_tolerance = heading > 0 ? 60 : (double?)null
                }).ToArray(),
                costing = "motorcycle"
            };
            var results = await _flurlClient
                .Request("locate")
                .PostJsonAsync(request, cancellationToken: cancellationToken)
                .ReceiveJson<List<LocateResponse?>>();
            return results ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Locate API failed");
            return [];
        }
    }
    private async Task<Result> PostAsync<TRequest>(string endpoint, TRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Log.Debug("Requesting {Endpoint} from Valhalla", endpoint);
        try
        {
            RouteResponse? response = await _flurlClient
                .Request(endpoint)
                .PostJsonAsync(request, cancellationToken: cancellationToken)
                .ReceiveJson<RouteResponse>();

            if (response?.Trip == null)
            {
                Log.Warning("Valhalla returned OK but no trip data");
                return Result.Failure("Valhalla returned an empty response (no trip)");
            }

            Log.Debug("{Endpoint} succeeded: {Distance} km", endpoint, response.Trip.Summary?.Length ?? 0);
            return Result.Success(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "Error calling Valhalla {Endpoint} API", endpoint);
            return Result.Failure($"Error: {ex.Message}", ex);
        }
    }
}
