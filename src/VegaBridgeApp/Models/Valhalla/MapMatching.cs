using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

/// <summary>
/// Single GPS point in a trace for map matching.
/// </summary>
public class TracePoint
{
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }
}

/// <summary>
/// Map matching request payload for Valhalla's /map_match endpoint.
/// </summary>
public class MapMatchRequest
{
    [JsonPropertyName("shape")]
    public List<TracePoint> Shape { get; set; } = new();

    [JsonPropertyName("profile")]
    public string Profile { get; set; } = "auto";

    [JsonPropertyName("radius")]
    public double Radius { get; set; } = 50;

    [JsonPropertyName("search_radius")]
    public double? SearchRadius { get; set; }

    [JsonPropertyName("batch_size")]
    public int? BatchSize { get; set; }

    [JsonPropertyName("options")]
    public MapMatchOptions? Options { get; set; }
}

/// <summary>
/// Options for map matching (confidence, strictness, etc.).
/// </summary>
public class MapMatchOptions
{
    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("strictness")]
    public double? Strictness { get; set; }

    [JsonPropertyName("generalize")]
    public double? Generalize { get; set; }
}

/// <summary>
/// Matched point result from Valhalla map_match.
/// </summary>
public class MapMatchedPoint
{
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }

    [JsonPropertyName("edge_index")]
    public int? EdgeIndex { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("distance")]
    public double? Distance { get; set; }

    [JsonPropertyName("time")]
    public int? Time { get; set; }

    [JsonPropertyName("path")]
    public List<MapMatchedPoint>? Path { get; set; }
}

/// <summary>
/// Full map matching response from Valhalla.
/// </summary>
public class MapMatchResponse
{
    [JsonPropertyName("matched")]
    public List<MapMatchedPoint>? Matched { get; set; }

    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; set; }

    [JsonPropertyName("cost")]
    public MapMatchCost? Cost { get; set; }
}

/// <summary>
/// Cost breakdown for map matching (optional).
/// </summary>
public class MapMatchCost
{
    [JsonPropertyName("data_cost")]
    public double? DataCost { get; set; }

    [JsonPropertyName("match_cost")]
    public double? MatchCost { get; set; }

    [JsonPropertyName("total_cost")]
    public double? TotalCost { get; set; }
}

/// <summary>
/// Wrapper result class for map matching operations.
/// </summary>
public class MapMatchResult
{
    public MapMatchResult()
    {
    }

    public MapMatchResult(List<MapMatchedPoint> matchedPoints)
    {
        MatchedPoints = matchedPoints;
    }

    public MapMatchResult(List<Coordinate> fallbackPoints)
    {
        // Convert fallback points to matched points with 0 confidence
        MatchedPoints = fallbackPoints.Select(p => new MapMatchedPoint
        {
            Lat = p.Latitude,
            Lon = p.Longitude,
            Confidence = 0.0
        }).ToList();
    }

    /// <summary>
    /// Best matched points (confidence-weighted).
    /// </summary>
    public List<MapMatchedPoint> MatchedPoints { get; set; } = new();

    /// <summary>
    /// Number of matched points (may be less than input if some were discarded).
    /// </summary>
    public int MatchedCount => MatchedPoints.Count;

    /// <summary>
    /// Average confidence across all matched points.
    /// </summary>
    public double AverageConfidence => MatchedPoints.Count > 0
        ? MatchedPoints.Average(m => m.Confidence ?? 0)
        : 0;

    /// <summary>
    /// Get the "best" matched point (highest confidence) for a given input index.
    /// </summary>
    public MapMatchedPoint? GetBestMatchedPoint(int inputIndex, int radiusMeters = 50)
    {
        // Simple radius-based lookup
        var nearby = MatchedPoints
            .Where(m => Math.Abs(m.Lat - inputIndex) <= radiusMeters)
            .OrderByDescending(m => m.Confidence)
            .FirstOrDefault();

        return nearby;
    }
}
