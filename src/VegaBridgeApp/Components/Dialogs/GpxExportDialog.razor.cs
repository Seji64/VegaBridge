using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace VegaBridgeApp.Components.Dialogs;

/// <summary>
/// Dialog that asks the user whether to export as track (&lt;trk&gt;) or route (&lt;rte&gt;).
/// </summary>
public partial class GpxExportDialog
{
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = null!;

    private string _choice = "track";

    private void Cancel() => MudDialog.Cancel();

    private void Submit()
    {
        MudDialog.Close(DialogResult.Ok(_choice));
    }
}
