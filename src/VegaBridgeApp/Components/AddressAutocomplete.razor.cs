using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using VegaBridgeApp.Models.Geocoding;

namespace VegaBridgeApp.Components;

/// <summary>
/// Address autocomplete with pinned locations (Current Position / Home)
/// shown above the search results. Owns its instance so pins can be
/// selected via SelectOptionAsync (which sets the value and closes the menu).
/// </summary>
public partial class AddressAutocomplete
{
    // Highlight typed terms in bold instead of the default yellow <mark>.
    private const string MatchStyle = "background-color: transparent; color: inherit; font-weight: 700; padding: 0;";

    private static readonly char[] QuerySeparators = [' ', ','];

    private MudAutocomplete<GeoResult>? _auto;

    // Fixed input id: the suggestions are rendered in the popover provider,
    // outside the autocomplete, so the fill-in JS can't find the input via the DOM tree.
    private readonly string _inputId = $"address-{Guid.NewGuid():N}";

    private string[] _queryTerms = [];

    [Inject] private IJSRuntime JS { get; set; } = null!;

    [Parameter] public GeoResult? Value { get; set; }

    [Parameter] public EventCallback<GeoResult?> ValueChanged { get; set; }

    [Parameter, EditorRequired] public Func<string?, CancellationToken, Task<IEnumerable<GeoResult>>> SearchFunc { get; set; } = null!;

    [Parameter] public IEnumerable<GeoResult> Pins { get; set; } = [];

    [Parameter] public string? Label { get; set; }

    [Parameter] public string? AdornmentIcon { get; set; }

    [Parameter] public MudBlazor.Color AdornmentColor { get; set; } = MudBlazor.Color.Secondary;

    [Parameter] public EventCallback<MouseEventArgs> OnAdornmentClick { get; set; }

    [Parameter] public Margin Margin { get; set; } = Margin.Dense;

    [Parameter] public int MinCharacters { get; set; }

    [Parameter] public string? Class { get; set; }

    private Task<IEnumerable<GeoResult>> SearchAsync(string? query, CancellationToken ct)
    {
        _queryTerms = query?.Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries) ?? [];
        return SearchFunc(query, ct);
    }

    /// <summary>
    /// "Fill in" arrow (as in Google Maps): continues the search from the
    /// suggestion instead of selecting it, e.g. to add a house number.
    /// </summary>
    private async Task FillInAsync(GeoResult suggestion)
    {
        (string text, int caret) = suggestion.GetFillIn();
        await JS.InvokeVoidAsync("addressAutocomplete.fillIn", _inputId, text, caret);
    }

    private async Task SelectPinAsync(GeoResult pin)
    {
        // _auto is an @ref field – null before the first render; the
        // callback can fire from the pinned-locations template.
        if (_auto == null) return;
        await _auto.SelectOptionAsync(pin);
    }
}
