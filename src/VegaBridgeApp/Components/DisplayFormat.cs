using Microsoft.Extensions.Localization;
using VegaBridgeApp.Utils;

namespace VegaBridgeApp.Components;

/// <summary>
/// Localized texts for distances, durations and times shown in the UI.
/// </summary>
public static class DisplayFormat
{
    /// <summary>"&lt; 1 Min", "42 Min" or "6 Std 44 Min"; "–" when unknown.</summary>
    public static string FormatDuration(this IStringLocalizer localizer, double minutes)
    {
        if (minutes < 0) return "–";
        if (minutes < 1) return localizer["NavLessThanMinute"];

        (int hours, int mins) = DisplayRounding.Duration(minutes);
        return hours == 0
            ? string.Format(localizer["NavMin"], mins)
            : string.Format(localizer["NavHoursMinutes"], hours, mins);
    }

    /// <summary>Distance to the next turn: "20 m", "350 m", "1,2 km"; "–" when unknown.</summary>
    public static string FormatTurnDistance(this IStringLocalizer localizer, double? meters)
    {
        if (meters is not { } m || m < 0) return "–";

        (double value, bool isKm) = DisplayRounding.TurnDistance(m);
        return string.Format(isKm ? localizer["Km"] : localizer["Meter"], value);
    }

    /// <summary>Clock time of arrival when riding <paramref name="minutes"/> from now; "–" when unknown.</summary>
    public static string FormatArrivalTime(double minutes) =>
        minutes < 0 ? "–" : DateTime.Now.AddMinutes(minutes).ToString("t");
}
