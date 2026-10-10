using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

/// <summary>
/// Error body Valhalla sends with HTTP 400, e.g.
/// <c>{"error_code":442,"error":"No path could be found for input"}</c>.
/// </summary>
public class ValhallaError
{
    /// <summary>"No path could be found for input".</summary>
    public const int NoPath = 442;

    [JsonPropertyName("error_code")]
    public int ErrorCode { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
