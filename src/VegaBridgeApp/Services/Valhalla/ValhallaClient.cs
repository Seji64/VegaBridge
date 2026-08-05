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
                 Log.Warning("Valhalla returned OK but with no trip data");
                 return Result.Failure("Valhalla returned an empty response (no trip)");
             }
            
             Log.Information(
                 "Route received: {Distance} km, {Time} s, {LegCount} leg(s)",
                 response.Trip.Summary?.Length,
                 response.Trip.Summary?.Time,
                 response.Trip.Legs?.Count ?? 0);
            
             return Result.Success(response);
        }
        // ── 400 – invalid request (not retried) ──
        catch (FlurlHttpException ex) when (ex.StatusCode == (int)HttpStatusCode.BadRequest)
        {
            string? errorBody = await TryGetErrorBody(ex);
            Log.Error(ex, "Valhalla 400: {Error}", errorBody);
            return Result.Failure($"Invalid route request (400): {errorBody ?? "unknown error"}", ex);
        }
        // ── 404 – wrong URL (not retried) ──
        catch (FlurlHttpException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
        {
            Log.Error(ex, "Valhalla 404 – check base URL");
            return Result.Failure("Valhalla endpoint not found (404) – check the base URL", ex);
        }
        // ── 429 – rate‑limited (not retried – caller should back off) ──
        catch (FlurlHttpException ex) when (ex.StatusCode == (int)HttpStatusCode.TooManyRequests)
        {
            Log.Error(ex, "Valhalla 429 – rate limit hit");
            return Result.Failure("Too many requests (429) – try again later", ex);
        }
        // ── Transient errors – these have already been retried by the resilience handler ──
        catch (FlurlHttpTimeoutException ex)
        {
            Log.Error(ex, "Valhalla timed out after all retries");
            return Result.Failure(
                "Valhalla did not respond – check your network or the server URL", ex);
        }
        catch (FlurlParsingException ex)
        {
            Log.Error(ex, "Failed to parse Valhalla response");
            return Result.Failure($"Failed to parse Valhalla response: {ex.Message}", ex);
        }
        catch (FlurlHttpException ex)
        {
            string? errorBody = await TryGetErrorBody(ex);
            Log.Error(ex, "Valhalla HTTP error {Status}: {Error}", ex.StatusCode, errorBody);
            return Result.Failure(
                $"Valhalla returned HTTP {ex.StatusCode}: {errorBody ?? ex.Message}", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Error("Valhalla request timed out");
            return Result.Failure("Valhalla request timed out");
        }
        catch (OperationCanceledException)
        {
            Log.Information("Valhalla request was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected error calling Valhalla API");
            return Result.Failure($"Unexpected error: {ex.Message}", ex);
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
