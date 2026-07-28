using Microsoft.AspNetCore.Components;
using MudBlazor;
using VegaBridgeApp.Models.Routes;

namespace VegaBridgeApp.Components.Dialogs;

/// <summary>
/// Dialog shown when a GPX file contains both a track (<c>&lt;trk&gt;</c>)
/// and a route (<c>&lt;rte&gt;</c>). The user picks which one to import.
/// </summary>
public partial class GpxImportDialog
{
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = null!;

    /// <summary>
    /// The parse result passed into the dialog by the caller via <see cref="DialogParameters"/>.
    /// </summary>
    [Parameter] public GpxParseResult ParseResult { get; set; } = null!;

    private string _choice = "";

    protected override void OnInitialized()
    {
        _choice = ParseResult.HasTrack ? "track" : "route";
    }

    private void Cancel() => MudDialog.Cancel();

    private void Submit()
    {
        GpxImportChoice result = new()
        {
            UseTrack = _choice == "track",
            UseRoute = _choice == "route"
        };
        MudDialog.Close(DialogResult.Ok(result));
    }
}

/// <summary>
/// Result returned from <see cref="GpxImportDialog"/>.
/// </summary>
public class GpxImportChoice
{
    public bool UseTrack { get; init; }
    public bool UseRoute { get; init; }
}
