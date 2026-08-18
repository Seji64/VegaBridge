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

    /// <summary>Initial bearing (great-circle) from point A to point B, in degrees 0-360.</summary>
    public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        double phi1 = ToRad(lat1);
        double phi2 = ToRad(lat2);
        double dLon = ToRad(lon2 - lon1);
        double y = Math.Sin(dLon) * Math.Cos(phi2);
        double x = Math.Cos(phi1) * Math.Sin(phi2) -
                   Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLon);
        return (ToDeg(Math.Atan2(y, x)) + 360.0) % 360.0;
    }

    /// <summary>
    /// Inserts intermediate points on long polyline segments so no segment
    /// exceeds <paramref name="maxSegmentM"/> meters. Reduces chord error
    /// in curves — without this, a 100m curve segment can be 30-40m from
    /// the actual road, causing false off-route detection.
    /// </summary>
    public static List<Coordinate> DensifyPolyline(IReadOnlyList<Coordinate> coords, double maxSegmentM = 20.0)
    {
        if (coords.Count < 2) return [.. coords];

        List<Coordinate> result = [coords[0]];
        for (int i = 1; i < coords.Count; i++)
        {
            Coordinate a = coords[i - 1];
            Coordinate b = coords[i];
            double dist = DistanceMeters(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
            if (dist > maxSegmentM)
            {
                int n = (int)Math.Ceiling(dist / maxSegmentM);
                for (int j = 1; j < n; j++)
                {
                    double t = (double)j / n;
                    double lat = a.Latitude + t * (b.Latitude - a.Latitude);
                    double lon = a.Longitude + t * (b.Longitude - a.Longitude);
                    result.Add(new Coordinate(lat, lon, null));
                }
            }
            result.Add(b);
        }
        return result;
    }
}
