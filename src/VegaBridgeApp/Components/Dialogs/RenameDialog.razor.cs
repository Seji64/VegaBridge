using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace VegaBridgeApp.Components.Dialogs;

/// <summary>
/// Simple text-input dialog for renaming items.
/// </summary>
public partial class RenameDialog
{
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = null!;

    [Parameter] public string Title { get; set; } = "";
    [Parameter] public string ContentText { get; set; } = "";
    [Parameter] public string ButtonText { get; set; } = "OK";
    [Parameter] public MudBlazor.Color Color { get; set; } = MudBlazor.Color.Primary;

    private string _newName = "";

    private void Cancel()
    {
        try { MudDialog.Cancel(); } catch { /* dialog may already be closed */ }
    }

    private void Submit()
    {
        try { MudDialog.Close(DialogResult.Ok(_newName)); }
        catch
        {
            // ignored
        }
    }
}
