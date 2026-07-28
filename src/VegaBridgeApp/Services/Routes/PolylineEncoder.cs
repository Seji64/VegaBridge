using VegaBridgeApp.Models.Valhalla;

namespace VegaBridgeApp.Services.Routes;

/// <summary>
/// Encodes a list of coordinates into Google polyline6 format.
/// Polyline6 uses 1e6 precision (6 decimal places), suitable for high-precision encoding.
/// </summary>
public static class PolylineEncoder
{
    /// <summary>
    /// Encodes a list of coordinates into a polyline6 string.
    /// </summary>
    /// <param name="coordinates">The ordered list of coordinates to encode.</param>
    /// <returns>A polyline6-encoded string, or an empty string if the input is null or empty.</returns>
    public static string EncodePolyline6(IReadOnlyList<Coordinate>? coordinates)
    {
        if (coordinates is null || coordinates.Count == 0)
            return string.Empty;

        const double precisionFactor = 1e6;

        long previousLatitude = 0;
        long previousLongitude = 0;

        List<char> encodedChars = [];

        foreach (Coordinate coordinate in coordinates)
        {
            double lat = coordinate.Latitude;
            double lon = coordinate.Longitude;

            if (double.IsNaN(lat) || double.IsNaN(lon) ||
                double.IsInfinity(lat) || double.IsInfinity(lon))
                continue;

            long currentLatitude = (long)Math.Round(lat * precisionFactor);
            long currentLongitude = (long)Math.Round(lon * precisionFactor);

            EncodeSignedValue(currentLatitude - previousLatitude, encodedChars);
            EncodeSignedValue(currentLongitude - previousLongitude, encodedChars);

            previousLatitude = currentLatitude;
            previousLongitude = currentLongitude;
        }

        return new string([.. encodedChars]);
    }

    /// <summary>
    /// Encodes a signed integer difference into a series of base64 characters.
    /// </summary>
    private static void EncodeSignedValue(long value, List<char> output)
    {
        // Shift left by 1 and invert if negative
        long encoded = value << 1;

        if (value < 0)
            encoded = ~encoded;

        // Split into 5-bit chunks and encode
        while (encoded >= 0x20)
        {
            long chunk = 0x20 | (encoded & 0x1F);
            output.Add((char)(chunk + 63));
            encoded >>= 5;
        }

        output.Add((char)(encoded + 63));
    }

    /// <summary>
    /// Decodes a polyline6 string into a list of coordinates.
    /// </summary>
    /// <param name="polyline">The polyline6-encoded string.</param>
    /// <returns>A list of coordinates, or an empty list if the input is null or empty.</returns>
    public static List<Coordinate> DecodePolyline6(string? polyline)
    {
        if (string.IsNullOrEmpty(polyline))
            return [];

        const double precisionFactor = 1e6;
        List<Coordinate> coordinates = [];
        int index = 0;
        long latitude = 0;
        long longitude = 0;

        while (index < polyline.Length)
        {
            // Decode latitude
            int shift = 0;
            long result = 0;
            bool byteFinished = false;
            while (!byteFinished && index < polyline.Length)
            {
                char c = polyline[index++];
                int b = c - 63;
                if (b >= 0x20)
                {
                    result |= (long)(b & 0x1F) << shift;
                    shift += 5;
                }
                else
                {
                    result |= (long)(b & 0x1F) << shift;
                    byteFinished = true;
                }
            }

            if (index >= polyline.Length && !byteFinished) break;

            long finalLat = ((result >> 1) ^ (-(result & 1)));
            latitude += finalLat;

            // Decode longitude
            shift = 0;
            result = 0;
            byteFinished = false;
            while (!byteFinished && index < polyline.Length)
            {
                char c = polyline[index++];
                int b = c - 63;
                if (b >= 0x20)
                {
                    result |= (long)(b & 0x1F) << shift;
                    shift += 5;
                }
                else
                {
                    result |= (long)(b & 0x1F) << shift;
                    byteFinished = true;
                }
            }
            long finalLon = ((result >> 1) ^ (-(result & 1)));
            longitude += finalLon;

            coordinates.Add(new Coordinate(latitude / precisionFactor, longitude / precisionFactor, null));
        }

        return coordinates;
    }
}