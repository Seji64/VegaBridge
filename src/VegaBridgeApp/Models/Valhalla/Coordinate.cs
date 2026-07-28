namespace VegaBridgeApp.Models.Valhalla;

/// <summary>
/// Simple decoded coordinate (lat/lon).
/// </summary>
public readonly record struct Coordinate(double Latitude, double Longitude, string? Label);
