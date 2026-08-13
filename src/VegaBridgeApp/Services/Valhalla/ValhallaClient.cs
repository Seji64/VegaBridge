using System.Net;
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
    private const string HttpClientName = "Valhalla";

    private readonly FlurlClient _flurlClient;

    public ValhallaClient(IHttpClientFactory httpClientFactory)
    {
        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
        _flurlClient = new FlurlClient(httpClient);
    }

    /// <inheritdoc />
    public async Task<Result> GetRouteAsync(RouteRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Log.Debug("Requesting route from Valhalla");
        try
        {
            RouteResponse? response = await _flurlClient
                .Request("route")
                .PostJsonAsync(request, cancellationToken: cancellationToken)
                .ReceiveJson<RouteResponse>();

            if (response?.Trip == null)
            {
                Log.Warning("Valhalla returned OK but no trip data");
                return Result.Failure("Valhalla returned an empty response (no trip)");
            }
            Log.Information("Route received: {Distance} km, {Time} s", response.Trip.Summary?.Length, response.Trip.Summary?.Time);
            return Result.Success(response);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error calling Valhalla route API");
            return Result.Failure($"Error: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<Result> GetMapMatchAsync(TraceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Log.Debug("Requesting map‑matching from Valhalla");
        try
        {
            request.CostingOptions ??= new Dictionary<string, object>();
            request.CostingOptions["shape_match"] = "map_snap";
            RouteResponse? response = await _flurlClient
                .Request("trace_route")
                .PostJsonAsync(request, cancellationToken: cancellationToken)
                .ReceiveJson<RouteResponse>();
            if (response?.Trip == null)
            {
                Log.Warning("Valhalla map‑matching returned OK but no trip data");
                return Result.Failure("Valhalla map‑matching empty response");
            }
            Log.Information("Map‑matching succeeded: {Dist} km", response.Trip.Summary?.Length ?? 0);
            return Result.Success(response);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Valhalla map‑matching error");
            return Result.Failure($"Map‑matching error: {ex.Message}", ex);
        }
    }

    private static async Task<string?> TryGetErrorBody(FlurlHttpException ex)
    {
        try
        {
            return await ex.GetResponseStringAsync();
        }
        catch
        {
            return null;
        }
    }
}
