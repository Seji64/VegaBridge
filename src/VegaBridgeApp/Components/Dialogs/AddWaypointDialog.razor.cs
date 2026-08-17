using Microsoft.AspNetCore.Components;
using MudBlazor;
using VegaBridgeApp.Models.Geocoding;

namespace VegaBridgeApp.Components.Dialogs;

/// <summary>
/// Confirmation dialog for adding a map-clicked position as a waypoint.
/// </summary>
public partial class AddWaypointDialog
{
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = null!;

    [Parameter] public string Title { get; set; } = "";
    [Parameter] public GeoResult? Location { get; set; }

    private static string FormatCoordinate(double? lat, double? lon)
    {
        if (lat == null || lon == null) return "–";
        return $"{lat.Value:F5}, {lon.Value:F5}";
    }

    private void Cancel()
    {
        try { MudDialog.Cancel(); } catch { /* dialog may already be closed */ }
    }

    private void Add()
    {
        try { MudDialog.Close(DialogResult.Ok(true)); }
        catch
        {
            // ignored
        }
    }
}
