using Microsoft.AspNetCore.Components;
using MudBlazor;
using VegaBridgeApp.Models.Geocoding;

namespace VegaBridgeApp.Components.Dialogs;

/// <summary>
/// Actions for an existing waypoint: move, delete or cancel.
/// Closes with DialogResult.Ok("move" | "delete") or Cancel.
/// </summary>
public partial class WaypointActionsDialog
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

    private void Move()
    {
        try { MudDialog.Close(DialogResult.Ok("move")); }
        catch
        {
            // ignored
        }
    }

    private void Delete()
    {
        try { MudDialog.Close(DialogResult.Ok("delete")); }
        catch
        {
            // ignored
        }
    }
}
