using Microsoft.AspNetCore.Components;
using MudBlazor;
using CommunityToolkit.Maui.Storage;
using VegaBridgeApp.Components.Dialogs;
using VegaBridgeApp.Models.Routes;
using VegaBridgeApp.Models.Valhalla;
using VegaBridgeApp.Services.Routes;
using VegaBridgeApp.Utils;

namespace VegaBridgeApp.Components.Pages;

public partial class MyRoutes : ComponentBase
{
    private List<SavedRoute>? _routes;

    protected override async Task OnInitializedAsync()
    {
        await LoadRoutesAsync();
    }

    private async Task LoadRoutesAsync()
    {
        _routes = await RouteStorage.GetAllRoutesAsync();
        StateHasChanged();
    }

    // ── Delete ──────────────────────────────────────────────────────────

    private async Task DeleteRoute(SavedRoute route)
    {
        await RouteStorage.DeleteRouteAsync(route.Id);
        await LoadRoutesAsync();
        Snackbar.Add(L["RouteDeleted"], Severity.Success);
    }

    // ── Rename ──────────────────────────────────────────────────────────

    private async Task RenameRoute(SavedRoute route)
    {
        try
        {
            DialogParameters parameters = new()
            {
                { nameof(RenameDialog.ContentText), (string)L["RenameDialogPrompt"] },
                { nameof(RenameDialog.ButtonText), (string)L["RenameDialogSave"] },
                { "Color", MudBlazor.Color.Primary }
            };
            IDialogReference dialog = await DialogService.ShowAsync<RenameDialog>(
                L["RenameDialogTitle"], parameters);
            DialogResult? result = await dialog.Result;

            if (result is not { Canceled: true } && result?.Data is string newName && !string.IsNullOrWhiteSpace(newName))
            {
                route.Name = newName;
                await RouteStorage.SaveRouteAsync(route);
                await LoadRoutesAsync();
                Snackbar.Add(L["RouteRenamed"], Severity.Success);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Fehler beim Umbenennen: {ex.Message}", Severity.Error);
        }
    }

    // ── GPX Import ──────────────────────────────────────────────────────

    private async Task ImportGpx()
    {
        try
        {
            FileResult? file = await FilePicker.Default.PickAsync();
            if (file == null) return;

            await using Stream stream = await file.OpenReadAsync();

            GpxParseResult parsed = await GpxService.ParseGpxAsync(stream);
            if (!parsed.IsValid)
            {
                Snackbar.Add(L["GPXInvalid"], Severity.Error);
                return;
            }

            List<Coordinate>? points;
            string name;

            if (parsed.IsAmbiguous)
            {
                DialogParameters parameters = new()
                {
                    { nameof(GpxImportDialog.ParseResult), parsed }
                };
                IDialogReference dialog = await DialogService.ShowAsync<GpxImportDialog>("", parameters);
                DialogResult? result = await dialog.Result;

                if (result?.Canceled == true || result.Data is not GpxImportChoice choice)
                    return;

                points = choice.UseTrack ? parsed.TrackPoints : parsed.RoutePoints;
                name = choice.UseTrack ? parsed.TrackName : parsed.RouteName;
            }
            else if (parsed.HasTrack)
            {
                points = parsed.TrackPoints;
                name = parsed.TrackName;
            }
            else
            {
                points = parsed.RoutePoints;
                name = parsed.RouteName;
            }

            if (points == null || points.Count < 2)
            {
                Snackbar.Add(L["GPXTooFewPoints"], Severity.Error);
                return;
            }

            SavedRoute route = BuildRoute(name, points);
            await RouteStorage.SaveRouteAsync(route);
            await LoadRoutesAsync();
            Snackbar.Add(L["GPXImported"], Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add(string.Format(L["ImportError"], ex.Message), Severity.Error);
        }
    }

    // ── GPX Export ──────────────────────────────────────────────────────

    private async Task ExportGpx(SavedRoute route)
    {
        try
        {
            IDialogReference dialog = await DialogService.ShowAsync<GpxExportDialog>("");
            DialogResult? result = await dialog.Result;

            if (result?.Canceled == true || result.Data is not string choice)
                return;

            bool asTrack = choice == "track";
            string format = asTrack ? "Track" : "Route";

            await using Stream stream = await GpxService.ExportPointsAsync(
                route.Name, route.Waypoints ?? [], asTrack);

            string filename = $"{route.Name.Replace(" ", "_")}.gpx";
            FileSaverResult saveResult = await FileSaver.Default.SaveAsync(filename, stream, CancellationToken.None);

            if (saveResult.IsSuccessful)
            {
                Snackbar.Add(string.Format(L["GPXExported"], format), Severity.Success);
            }
            else
            {
                Snackbar.Add(saveResult.Exception?.Message ?? L["GPXExportCancelled"], Severity.Error);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add(string.Format(L["GPXExportFailed"], ex.Message), Severity.Error);
        }
    }

    // ── Navigation ──────────────────────────────────────────────────────

    private void NavigateToMap(SavedRoute route)
    {
        NavManager.NavigateTo($"/map/{route.Id}");
    }

    private void CreateNewRoute()
    {
        NavManager.NavigateTo("/map");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static SavedRoute BuildRoute(string name, List<Coordinate> points)
    {
        string polyline6 = PolylineEncoder.EncodePolyline6(points);
        double totalKm = GeoMath.TotalDistanceKm(points);

        return new SavedRoute
        {
            Name = name,
            Waypoints = points,
            Polyline6 = polyline6,
            DistanceKm = Math.Round(totalKm, 2),
            TimeMinutes = Math.Round((totalKm / 50.0) * 60.0, 1),
            CreatedAt = DateTime.UtcNow
        };
    }
}
