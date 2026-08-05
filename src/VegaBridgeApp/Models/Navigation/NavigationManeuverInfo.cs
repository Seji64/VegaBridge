namespace VegaBridgeApp.Models.Navigation;

public class NavigationManeuverInfo
{
    public int Index { get; init; }
    public int Total { get; init; }
    public int ValhallaType { get; init; }
    public string Instruction { get; init; } = "";
    public List<string> StreetNames { get; init; } = [];
    public double LengthKm { get; init; }
    public double TimeMin { get; init; }
    public double? TurnDegree { get; init; }
    public int? RoundaboutExitCount { get; init; }
    public string? TravelMode { get; init; }
    public string? TravelType { get; init; }
    public int? RoundaboutExit { get; init; }
}
