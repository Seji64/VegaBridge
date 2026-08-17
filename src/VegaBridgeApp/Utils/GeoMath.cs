using VegaBridgeApp.Models.Valhalla;

namespace VegaBridgeApp.Utils;

/// <summary>
/// Geographic math utilities – Haversine, distance, coordinate helpers.
/// Single source of truth for all distance calculations.
/// </summary>
public static class GeoMath
{
    private const double EarthRadiusM = 6371000.0;

    /// <summary>Haversine distance in meters between two lat/lon points.</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = ToRad(lat2 - lat1);
        double dLon = ToRad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return EarthRadiusM * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Haversine distance in kilometers between two lat/lon points.</summary>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
        => DistanceMeters(lat1, lon1, lat2, lon2) / 1000.0;

    /// <summary>Total Haversine distance along a sequence of coordinates, in km.</summary>
    public static double TotalDistanceKm(IReadOnlyList<Coordinate> coordinates)
    {
        if (coordinates.Count < 2) return 0;
        double total = 0;
        for (int i = 1; i < coordinates.Count; i++)
        {
            total += DistanceKm(
                coordinates[i - 1].Latitude, coordinates[i - 1].Longitude,
                coordinates[i].Latitude, coordinates[i].Longitude);
        }
        return total;
    }

    /// <summary>Degrees to radians.</summary>
    public static double ToRad(double degrees) => degrees * Math.PI / 180.0;

    /// <summary>Radians to degrees.</summary>
    public static double ToDeg(double radians) => radians * 180.0 / Math.PI;
}
