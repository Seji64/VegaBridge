using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using VegaBridgeApp.Models.Geocoding;

namespace VegaBridgeApp.Components;

/// <summary>
/// Waypoint autocomplete with pinned locations (Current Position / Home)
/// shown above the search results. Owns its instance so pins can be
/// selected via SelectOptionAsync (which sets the value and closes the menu).
/// </summary>
public partial class WaypointAutocomplete
{
    private MudAutocomplete<GeoResult>? _auto;

    [Parameter] public GeoResult? Location { get; set; }

    [Parameter] public EventCallback<GeoResult?> LocationChanged { get; set; }

    [Parameter] public Func<string?, CancellationToken, Task<IEnumerable<GeoResult>>> SearchFunc { get; set; } = null!;

    [Parameter] public IEnumerable<GeoResult> Pins { get; set; } = [];

    [Parameter] public EventCallback<MouseEventArgs> OnAdornmentClick { get; set; }

    private Task OnLocationChanged(GeoResult? value) => LocationChanged.InvokeAsync(value);

    private async Task SelectPinAsync(GeoResult pin)
    {
        // _auto is an @ref field – null before the first render; the
        // callback can fire from the pinned-locations template.
        if (_auto == null) return;
        await _auto.SelectOptionAsync(pin);
    }
}
