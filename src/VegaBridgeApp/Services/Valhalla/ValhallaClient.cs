using System.Net;
using Flurl.Http;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<ValhallaClient> _logger;

    public ValhallaClient(IHttpClientFactory httpClientFactory, ILogger<ValhallaClient> logger)
    {
        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
        _flurlClient = new FlurlClient(httpClient);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> GetRouteAsync(RouteRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug("Requesting route from Valhalla");

        try
        {
            RouteResponse? response = await _flurlClient
                 .Request("route")
                 .PostJsonAsync(request, cancellationToken: cancellationToken)
                 .ReceiveJson<RouteResponse>();
            
             if (response?.Trip == null)
             {
                 _logger.LogWarning("Valhalla returned OK but with no trip data");
                 return Result.Failure("Valhalla returned an empty response (no trip)");
             }
            
             _logger.LogInformation(
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
            _logger.LogError(ex, "Valhalla 400: {Error}", errorBody);
            return Result.Failure($"Invalid route request (400): {errorBody ?? "unknown error"}", ex);
        }
        // ── 404 – wrong URL (not retried) ──
        catch (FlurlHttpException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
        {
            _logger.LogError(ex, "Valhalla 404 – check base URL");
            return Result.Failure("Valhalla endpoint not found (404) – check the base URL", ex);
        }
        // ── 429 – rate‑limited (not retried – caller should back off) ──
        catch (FlurlHttpException ex) when (ex.StatusCode == (int)HttpStatusCode.TooManyRequests)
        {
            _logger.LogError(ex, "Valhalla 429 – rate limit hit");
            return Result.Failure("Too many requests (429) – try again later", ex);
        }
        // ── Transient errors – these have already been retried by the resilience handler ──
        catch (FlurlHttpTimeoutException ex)
        {
            _logger.LogError(ex, "Valhalla timed out after all retries");
            return Result.Failure(
                "Valhalla did not respond – check your network or the server URL", ex);
        }
        catch (FlurlParsingException ex)
        {
            _logger.LogError(ex, "Failed to parse Valhalla response");
            return Result.Failure($"Failed to parse Valhalla response: {ex.Message}", ex);
        }
        catch (FlurlHttpException ex)
        {
            string? errorBody = await TryGetErrorBody(ex);
            _logger.LogError(ex, "Valhalla HTTP error {Status}: {Error}", ex.StatusCode, errorBody);
            return Result.Failure(
                $"Valhalla returned HTTP {ex.StatusCode}: {errorBody ?? ex.Message}", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Valhalla request timed out");
            return Result.Failure("Valhalla request timed out");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Valhalla request was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error calling Valhalla API");
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
