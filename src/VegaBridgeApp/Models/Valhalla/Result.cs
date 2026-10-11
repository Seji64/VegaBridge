namespace VegaBridgeApp.Models.Valhalla;

/// <summary>
/// Wraps a Valhalla API response with success/failure semantics.
/// </summary>
public class Result
{
    public bool IsSuccess { get; init; }
    public RouteResponse? Response { get; init; }
    public string? ErrorMessage { get; init; }
    public Exception? Exception { get; init; }

    /// <summary>Valhalla's <c>error_code</c> when it rejected the request (see <see cref="ValhallaError"/>).</summary>
    public int? ErrorCode { get; init; }

    public static Result Success(RouteResponse response) =>
        new() { IsSuccess = true, Response = response };

    public static Result Failure(string message, Exception? ex = null, int? errorCode = null) =>
        new() { IsSuccess = false, ErrorMessage = message, Exception = ex, ErrorCode = errorCode };
}
