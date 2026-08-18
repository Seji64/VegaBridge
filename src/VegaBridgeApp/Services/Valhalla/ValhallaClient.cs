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
    public async Task<List<LocateResponse?>> LocateAsync(List<(double Lat, double Lon)> points, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Log.Debug("Locating {Count} points", points.Count);
        try
        {
            var request = new
            {
                locations = points.Select(p => new { lat = p.Lat, lon = p.Lon }).ToArray(),
                costing = "motorcycle"
            };
            string json = await _flurlClient
                .Request("locate")
                .PostJsonAsync(request, cancellationToken: cancellationToken)
                .ReceiveString();
            // locate returns a JSON array, one object per input location
            var results = System.Text.Json.JsonSerializer.Deserialize<List<LocateResponse?>>(json);
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
