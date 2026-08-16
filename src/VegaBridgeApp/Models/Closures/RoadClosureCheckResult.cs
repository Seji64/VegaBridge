namespace VegaBridgeApp.Models.Closures;

/// <summary>
/// Result of a road-closure check along a route.
/// </summary>
/// <param name="Closures">Closures found inside the corridor, nearest first.</param>
/// <param name="CheckedAt">Timestamp of the check.</param>
/// <param name="IsSuccess">False when the Overpass request failed (network, timeout, quota).</param>
/// <param name="ErrorMessage">Failure reason when <see cref="IsSuccess"/> is false.</param>
public sealed record RoadClosureCheckResult(
    IReadOnlyList<RoadClosure> Closures,
    DateTimeOffset CheckedAt,
    bool IsSuccess,
    string? ErrorMessage = null)
{
    public static RoadClosureCheckResult Failure(string message) =>
        new([], DateTimeOffset.UtcNow, IsSuccess: false, ErrorMessage: message);
}
