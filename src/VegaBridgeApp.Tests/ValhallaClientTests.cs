using System.Text.Json;
using Flurl.Http.Testing;
using NSubstitute;
using VegaBridgeApp.Models.Valhalla;
using VegaBridgeApp.Services.Valhalla;
using Xunit;

namespace VegaBridgeApp.Tests;

/// <summary>
/// <see cref="ValhallaClient"/> against faked Valhalla responses
/// (bodies as returned by valhalla1.openstreetmap.de).
/// </summary>
public class ValhallaClientTests
{
    private const string NoPathBody = """{"error_code":442,"error":"No path could be found for input","status_code":400,"status":"Bad Request"}""";
    private const string TripBody = """{"trip":{"summary":{"length":12.3,"time":900,"has_highway":false,"has_toll":false,"has_ferry":true}}}""";

    private readonly ValhallaClient _client;

    public ValhallaClientTests()
    {
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(ValhallaOptions.HttpClientName)
            .Returns(_ => new HttpClient { BaseAddress = new Uri("https://valhalla.test") });
        _client = new ValhallaClient(factory);
    }

    [Fact]
    public async Task NoPathWithHardExclusions_RetriesWithSoftAvoidanceOnly()
    {
        using HttpTest http = new();
        http.RespondWith(NoPathBody, 400).RespondWith(TripBody);

        Result result = await _client.GetRouteAsync(Request(CostingOptions.Avoiding(RoadAvoidance.Ferries)));

        Assert.True(result.IsSuccess);
        Assert.Equal(RoadAvoidance.Ferries, result.Response!.Trip!.Summary!.AvoidableRoads);
        Assert.Equal(2, http.CallLog.Count);
        JsonElement first = Motorcycle(http.CallLog[0].RequestBody);
        JsonElement retry = Motorcycle(http.CallLog[1].RequestBody);
        Assert.True(first.GetProperty("exclude_ferries").GetBoolean());
        Assert.Equal(JsonValueKind.Null, retry.GetProperty("exclude_ferries").ValueKind);
        Assert.Equal(0, retry.GetProperty("use_ferry").GetDouble());
    }

    [Fact]
    public async Task ValhallaError_IsReportedWithItsMessage()
    {
        using HttpTest http = new();
        http.RespondWith(NoPathBody, 400);

        Result result = await _client.GetRouteAsync(Request(costingOptions: null));

        Assert.False(result.IsSuccess);
        Assert.Equal(ValhallaError.NoPath, result.ErrorCode);
        Assert.Equal("No path could be found for input", result.ErrorMessage);
        Assert.Single(http.CallLog); // nothing to fall back to
    }

    [Fact]
    public async Task UnpavedKm_SumsRoughManeuversOfAllLegs()
    {
        using HttpTest http = new();
        http.RespondWith("""{"trip":{"legs":[{"maneuvers":[{"length":2.5},{"length":0.9,"rough":true}]},{"maneuvers":[{"length":1.2,"rough":true},{"length":0}]}]}}""");

        Result result = await _client.GetRouteAsync(Request(costingOptions: null));

        Assert.Equal(2.1, result.Response!.Trip!.UnpavedKm, 6);
    }

    [Fact]
    public async Task TraceAttributes_EdgeWalkOnTheShape_ReturnsTunnelEdges()
    {
        using HttpTest http = new();
        http.RespondWith("""{"units":"kilometers","edges":[{"way_id":20578351,"tunnel":false,"end_shape_index":2,"begin_shape_index":0,"length":0.009,"source_percent_along":0.882},{"way_id":203751280,"tunnel":true,"end_shape_index":281,"begin_shape_index":262,"length":2.425}],"alternate_paths":[]}""");

        TraceAttributesResponse? response = await _client.GetTraceAttributesAsync(new TraceAttributesRequest
        {
            EncodedPolyline = "abc",
            Filters = new TraceAttributesFilter { Attributes = ["edge.tunnel"] }
        });

        TraceEdge tunnel = Assert.Single(response!.Edges!, e => e.Tunnel);
        Assert.Equal((262, 281), (tunnel.BeginShapeIndex, tunnel.EndShapeIndex));
        JsonElement body = JsonDocument.Parse(http.CallLog[0].RequestBody).RootElement;
        Assert.EndsWith("/trace_attributes", http.CallLog[0].Request.Url.Path);
        Assert.Equal("edge_walk", body.GetProperty("shape_match").GetString());
        Assert.Equal("abc", body.GetProperty("encoded_polyline").GetString());
        Assert.Equal("include", body.GetProperty("filters").GetProperty("action").GetString());
    }

    [Fact]
    public async Task TraceAttributes_Rejected_ReturnsNull()
    {
        using HttpTest http = new();
        http.RespondWith("""{"error_code":443,"error":"Exact route match algorithm failed to find path","status_code":400,"status":"Bad Request"}""", 400);

        Assert.Null(await _client.GetTraceAttributesAsync(new TraceAttributesRequest { EncodedPolyline = "abc" }));
    }

    private static RouteRequest Request(Dictionary<string, CostingOptions>? costingOptions) => new()
    {
        Locations = [new Location { Lat = 54.44, Lon = 11.19 }, new Location { Lat = 54.65, Lon = 11.35 }],
        Costing = "motorcycle",
        CostingOptions = costingOptions
    };

    private static JsonElement Motorcycle(string requestBody) =>
        JsonDocument.Parse(requestBody).RootElement.GetProperty("costing_options").GetProperty("motorcycle");
}
