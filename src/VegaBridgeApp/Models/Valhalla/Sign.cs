using System.Text.Json.Serialization;

namespace VegaBridgeApp.Models.Valhalla;

public class Sign
{
    [JsonPropertyName("exit_number")]
    public List<int>? ExitNumber { get; set; }

    [JsonPropertyName("exit_branch")]
    public List<string>? ExitBranch { get; set; }

    [JsonPropertyName("exit_toward")]
    public List<string>? ExitToward { get; set; }

    [JsonPropertyName("exit_name")]
    public List<string>? ExitName { get; set; }
}
