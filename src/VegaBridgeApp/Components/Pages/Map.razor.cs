using Microsoft.AspNetCore.Components;
using OpenLayers.Blazor;
using VegaBridgeApp.Models.Geocoding;
using VegaBridgeApp.Models.Valhalla;
using VegaBridgeApp.Models.Routes;
using VegaBridgeApp.Services.Routes;
using VegaBridgeApp.Models.Navigation;
using VegaBridgeApp.Components.Dialogs;
using MudBlazor;
using Serilog;
using Shiny.Locations;
using VegaBridgeApp.Models.Utils;
using VegaBridgeApp.Services.Navigation;
using VegaBridgeApp.Services.Closures;
using VegaBridgeApp.Models.Closures;
using VegaBridgeApp.Utils;
using Coordinate = VegaBridgeApp.Models.Valhalla.Coordinate;
using Location = VegaBridgeApp.Models.Valhalla.Location;

namespace VegaBridgeApp.Components.Pages;

public partial class Map : ComponentBase, IAsyncDisposable, INavigationSink
{
    [Parameter]
    public string? RouteId { get; set; }
    
    private const string RouteLayerId = "route-layer";
    private const string BreadcrumbLayerId = "breadcrumb-layer";
    private const string ClosureLayerIdPrefix = "closure-overlay-";
    private const string ClosureHighlightColor = "#FFD600"; // yellow – clearly visible over the red route
    private const double ClosureHighlightRadiusM = 80;

    private OpenStreetMap? _map;
    private MudAutocomplete<GeoResult> _startAuto = null!;
    private MudAutocomplete<GeoResult> _destAuto = null!;

    private GeoResult? _startLocation;
    private GeoResult? _destinationLocation;
    private List<WaypointViewModel> _waypoints = [];
    private readonly List<(WaypointViewModel Waypoint, Marker Marker)> _waypointPins = [];
    private readonly HashSet<Guid> _skippedWaypointIds = [];
    private WaypointViewModel? _pendingMoveWaypoint;
    private DateTime _lastMarkerClickUtc = DateTime.MinValue;
    private bool _actionsDialogOpen;
    private string? _errorMessage;
    private bool _isLoading;
    private bool _isSaving;
    private RouteResponse? _currentRouteResponse;
    private string? _savedRouteId;
    private bool _mapLoaded = false;
    // Remember the user's zoom level so recalculating a route or a reroute
    // does not reset the view. Only user zooms after the first route display
    // are remembered (the initial Zoom="8" on first load must not win).
    private double? _savedZoom;
    private bool _hasShownRoute;

    // ── Track whether we've created the initial GPS marker ──
    private bool _gpsMarkerInitialized;
    private bool _disposed;
    private int _breadcrumbUpdating;
    private int _markerUpdating;
    private DateTime _lastUiRefresh = DateTime.MinValue;
    private DateTime _lastBreadcrumbUpdate = DateTime.MinValue;
    private DateTime _lastRerouteTime = DateTime.MinValue;
    private DateTime _lastMarkerUpdate = DateTime.MinValue;
    private double _lastMarkerLon;
    private double _lastMarkerLat;

    // ── Navigation state ──
    private NavigationManeuverInfo? _navManeuver;
    private NavigationStatus? _navStatus;
    private double _navProgress;

    // ── Navigation follow-view state (map tracks the position) ──
    private DateTime _lastNavFollowUpdate = DateTime.MinValue;
    private double _lastFollowLat;
    private double _lastFollowLon;
    private double _lastFollowHeading = -1;

    // ── Road closure check state ──
    private readonly List<RoadClosure> _closures = [];
    private bool _closureCheckInFlight;
    private DateTime _lastClosureCheckUtc = DateTime.MinValue;
    private bool _closureCheckRunning;

    protected override void OnInitialized()
    {
        // Subscribe to GPS position updates
        Gps.ReadingReceived += OnGpsReading;
        Gps.TrackingChanged += OnGpsTrackingChanged;

        // Subscribe to navigation events via sink
        NavService.AddSink(this);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Gps.RequestPermissionAsync();
            await WaitForMapLoadedAsync();
            await GetMyCurrentPosition();
            
            if (string.IsNullOrWhiteSpace(RouteId) && _startLocation == null && Gps.LastReading != null)
            {
                _startLocation = new GeoResult(
                    L["CurrentPos"],
                    Gps.LastReading.Position.Latitude,
                    Gps.LastReading.Position.Longitude,
                    "current");
            }
        }
    }
    
    protected override async Task OnParametersSetAsync()
    {
        if (!string.IsNullOrWhiteSpace(RouteId))
        {
            try
            {
                SavedRoute? savedRoute = await RouteStorage.GetRouteByIdAsync(RouteId);

                if (savedRoute != null && !string.IsNullOrEmpty(savedRoute.Polyline6) && savedRoute.Waypoints is { Count: >= 2 })
                {
                    await ShowRouteOnMapFromPolyline(savedRoute.Polyline6);

                    // Set start and destination based on first/last waypoints
                    Coordinate startCoord = savedRoute.Waypoints.First();
                    Coordinate endCoord = savedRoute.Waypoints.Last();

                    _startLocation = new GeoResult(startCoord.Label ?? "Start", startCoord.Latitude, startCoord.Longitude);
                    _destinationLocation = new GeoResult(endCoord.Label ?? "Ziel", endCoord.Latitude, endCoord.Longitude);

                    // Populate intermediate waypoints for UI (excluding first and last)
                    _waypoints = savedRoute.Waypoints
                        .Skip(1)
                        .Take(savedRoute.Waypoints.Count - 2)
                        .Select((coord, idx) => new WaypointViewModel
                        {
                            Location = new GeoResult(coord.Label ?? $"Waypoint {idx + 1}", coord.Latitude, coord.Longitude)
                        })
                        .ToList();

                    // Reset map center after waypoint list is ready (center on start)
                    if (_map != null)
                    {
                        await _map.SetCenter(new OpenLayers.Blazor.Coordinate(startCoord.Longitude, startCoord.Latitude));
                    }

                    Snackbar.Add(string.Format(L["RouteLoaded"], savedRoute.Name), Severity.Info);

                    // Automatically calculate the route via Valhalla (for starting navigation + turn-by-turn)
                    await CalculateRoute();
                }
                else
                {
                    Snackbar.Add(L["RouteLoadFailed"], Severity.Error);
                }
            }
            catch (Exception ex)
            {
                Snackbar.Add(L["RouteLoadFailed"], Severity.Error);
                Log.Error(ex.Message);
            }
        }
    }

    #region "Waypoint-Helpers"
    
    private void AddWaypoint()
    {
        _waypoints.Add(new WaypointViewModel());
        StateHasChanged();
    }

    private async Task RemoveWaypointAsync(int index)
    {
        if (index < 0 || index >= _waypoints.Count) return;
        _waypoints.RemoveAt(index);
        await SyncWaypointsAfterChangeAsync();
        StateHasChanged();
    }

    private async Task MoveWaypointAsync(WaypointViewModel waypoint, int direction)
    {
        int index = _waypoints.IndexOf(waypoint);
        int newIndex = index + direction;

        if (newIndex < 0 || newIndex >= _waypoints.Count) return;
        _waypoints.RemoveAt(index);
        _waypoints.Insert(newIndex, waypoint);
        await SyncWaypointsAfterChangeAsync();
        StateHasChanged();
    }

    private void OnWaypointChanged(WaypointViewModel waypoint, GeoResult? value)
    {
        waypoint.Location = value;
        _ = SyncWaypointsAfterChangeAsync();
    }

    /// <summary>
    /// Waypoint list changed: route shown → recalculate so pins/route reflect it,
    /// otherwise just refresh the pins.
    /// </summary>
    private async Task SyncWaypointsAfterChangeAsync()
    {
        if (_currentRouteResponse != null)
            await CalculateRoute();
        else
            await RefreshWaypointMarkersAsync();
    }

    /// <summary>
    /// Shows one green pin per waypoint. Skipped while a route is shown –
    /// route markers (green waypoint pins) are rebuilt by ShowRouteOnMap.
    /// </summary>
    private async Task RefreshWaypointMarkersAsync(bool force = false)
    {
        OpenStreetMap? map = _map;
        if (map == null || _disposed || (!force && _currentRouteResponse != null)) return;

        try
        {
            foreach ((_, Marker marker) in _waypointPins)
                map.MarkersList.Remove(marker);
            _waypointPins.Clear();

            foreach (WaypointViewModel waypoint in _waypoints)
            {
                if (waypoint.Location == null) continue;
                if (_skippedWaypointIds.Contains(waypoint.Id)) continue;
                Marker marker = new(MarkerType.MarkerPin,
                    new OpenLayers.Blazor.Coordinate(waypoint.Location.Longitude, waypoint.Location.Latitude),
                    "", PinColor.Blue);
                map.MarkersList.Add(marker);
                _waypointPins.Add((waypoint, marker));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh waypoint markers");
        }
    }

    /// <summary>
    /// Tap on a waypoint pin: offer move / delete.
    /// </summary>
    private async Task OnMarkerClick(Marker marker)
    {
        if (_disposed || NavService.IsNavigating || _pendingMoveWaypoint != null) return;
        // Overlapping pins (e.g. added twice at the same spot) fire this twice.
        if (_actionsDialogOpen) return;

        (WaypointViewModel Waypoint, Marker Marker)? pin = _waypointPins.FirstOrDefault(p => p.Marker == marker);
        if (pin == null || pin.Value.Waypoint.Location == null) return;

        int index = _waypoints.IndexOf(pin.Value.Waypoint);
        if (index < 0) return;

        // Claim this tap so OnMapClick (which also fires on marker taps) backs off.
        _lastMarkerClickUtc = DateTime.UtcNow;
        DialogParameters<WaypointActionsDialog> parameters = new()
        {
            { nameof(WaypointActionsDialog.Location), pin.Value.Waypoint.Location }
        };
        DialogOptions options = new()
        {
            Position = DialogPosition.BottomCenter,
            MaxWidth = MaxWidth.Small,
            FullWidth = true,
            CloseButton = true
        };

        _actionsDialogOpen = true;
        try
        {
            IDialogReference dialog = await DialogService.ShowAsync<WaypointActionsDialog>(L["Waypoint"], parameters, options);
            DialogResult? result = await dialog.Result;
        if (result is { Canceled: false, Data: string action })
        {
            switch (action)
            {
                case "move":
                    _pendingMoveWaypoint = pin.Value.Waypoint;
                    Snackbar.Add(L["MapClickMoveHint"], Severity.Info);
                    break;
                case "delete":
                    await DeleteWaypointAsync(index);
                    break;
            }
        }
        }
        finally
        {
            _actionsDialogOpen = false;
        }
    }

    private async Task DeleteWaypointAsync(int index)
    {
        await RemoveWaypointAsync(index);
        Snackbar.Add(L["WaypointRemoved"], Severity.Info);
    }

    /// <summary>
    /// Map zoom changed (user gesture or programmatic). Remember the level so
    /// route recalculations / reroutes keep the user's view. The initial
    /// Zoom="8" from the component markup is ignored until a route was shown.
    /// </summary>
    private void OnMapZoomChanged(double zoom)
    {
        if (!_hasShownRoute) return;
        _savedZoom = zoom;
    }

    /// <summary>
    /// Map tap: reverse-geocode the position and offer to add it as a waypoint.
    /// </summary>
    private async Task OnMapClick(OpenLayers.Blazor.Coordinate coordinate)
    {
        if (!_mapLoaded || NavService.IsNavigating || _isLoading || _disposed) return;

        try
        {
            double lon = coordinate.X;
            double lat = coordinate.Y;

            // Marker tap also fires OnClick – waypoint pins are handled by OnMarkerClick.
            if (_waypointPins.Any(p =>
                    p.Waypoint.Location != null &&
                    GeoMath.DistanceMeters(lat, lon, p.Waypoint.Location.Latitude, p.Waypoint.Location.Longitude) < WaypointTapRadiusM))
                return;

            // OnMarkerClick claims marker taps – back off if it fired for this tap
            // (marker taps raise OnClick too; the distance guard above may miss on
            // rounded coordinates, this one cannot).
            await Task.Delay(150);
            if ((DateTime.UtcNow - _lastMarkerClickUtc).TotalMilliseconds < 500)
                return;
            // A pending "move" consumes the next map tap.
            if (_pendingMoveWaypoint != null)
            {
                WaypointViewModel waypoint = _pendingMoveWaypoint;
                _pendingMoveWaypoint = null;

                List<GeoResult> moveReverse = await GeocodingService.GetReverseGeocodingAsync(lon, lat);
                waypoint.Location = new GeoResult(moveReverse.FirstOrDefault()?.Label ?? L["Waypoint"], lat, lon);

                if (_currentRouteResponse != null)
                    await CalculateRoute();
                else
                    await RefreshWaypointMarkersAsync();

                Snackbar.Add(L["WaypointMoved"], Severity.Success);
                return;
            }

            List<GeoResult> reverse = await GeocodingService.GetReverseGeocodingAsync(lon, lat);
            GeoResult location = new(reverse.FirstOrDefault()?.Label ?? L["Waypoint"], lat, lon);

            DialogParameters<AddWaypointDialog> parameters = new()
            {
                { nameof(AddWaypointDialog.Location), location }
            };
            DialogOptions options = new()
            {
                Position = DialogPosition.BottomCenter,
                MaxWidth = MaxWidth.Small,
                FullWidth = true,
                CloseButton = true
            };

            IDialogReference dialog = await DialogService.ShowAsync<AddWaypointDialog>(L["AddWaypoint"], parameters, options);
            DialogResult? result = await dialog.Result;
            if (result is { Canceled: false } && result.Data is true)
            {
                _waypoints.Add(new WaypointViewModel { Location = location });

                // Route shown → recalculate so the new waypoint is included and marked.
                if (_currentRouteResponse != null)
                    await CalculateRoute();
                else
                    await RefreshWaypointMarkersAsync();

                Snackbar.Add(L["WaypointAdded"], Severity.Success);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to add waypoint from map click");
        }
    }

    #endregion

    #region "GPS-Tracking"

    private async void OnGpsReading(GpsReading reading)
    {
        if (_disposed) return;

        // Default start = "Current position" on first GPS fix
        if (_startLocation == null && string.IsNullOrWhiteSpace(RouteId))
        {
            _startLocation = new GeoResult(
                L["CurrentPos"],
                reading.Position.Latitude,
                reading.Position.Longitude,
                "current");
            try { await InvokeAsync(StateHasChanged); } catch { }
        }

        // Update position marker & breadcrumb on the map (via JS interop, no Blazor re-render)
        if (_map != null && !_disposed)
            await UpdatePositionMarkerAsync(reading);

        // Throttled UI refresh: re-render at most every 500ms to avoid map re-creation
        if (_disposed) return;
        DateTime now = DateTime.UtcNow;
        if (!((now - _lastUiRefresh).TotalMilliseconds >= 500)) return;
        _lastUiRefresh = now;
        try { await InvokeAsync(StateHasChanged); } catch { /* component disposed */ }
    }

    private async void OnGpsTrackingChanged(bool isTracking)
    {
        if (_disposed) return;
        try {  await InvokeAsync(StateHasChanged); } catch { /* component disposed */ }
    }
    
    #endregion

    #region "Navigation Event Handlers

    public async Task OnManeuverAsync(NavigationManeuverInfo maneuver)
    {
        if (_disposed) return;
        _navManeuver = maneuver;
        _navProgress = maneuver.Total > 0
            ? (double)(maneuver.Index + 1) / maneuver.Total * 100
            : 0;
        await InvokeAsync(StateHasChanged);
    }

    public async Task OnStatusAsync(NavigationStatus status)
    {
        if (_disposed) return;
        _navStatus = status;
        int progressIndex = status.DisplayManeuverIndex > 0 ? status.DisplayManeuverIndex : status.CurrentManeuverIndex;
        _navProgress = status.TotalManeuvers > 0
            ? Math.Clamp((double)(progressIndex + 1) / status.TotalManeuvers * 100, 0, 100)
            : 0;
        await InvokeAsync(StateHasChanged);
    }

    public async Task OnFinishAsync()
    {
        if (_disposed) return;
        await InvokeAsync(() =>
        {
            Snackbar.Add(L["DestinationReached"], Severity.Success);
            StateHasChanged();
        });
    }

    public async Task OnCancelAsync()
    {
        if (_disposed) return;
        await InvokeAsync(() =>
        {
            Snackbar.Add(L["NavigationStopped"], Severity.Info);
            StateHasChanged();
        });
    }

    public async Task OnStartAsync(NavigationStartInfo start)
    {
        // UI does not need anything on start; maybe a short toast
        await InvokeAsync(StateHasChanged);
    }

    public async Task OnOffRouteAsync(double latitude, double longitude, double distanceMeters)
    {
        if (_disposed || _destinationLocation == null || _isLoading) return;
        if ((DateTime.UtcNow - _lastRerouteTime).TotalSeconds < 30) return;
        _lastRerouteTime = DateTime.UtcNow;

        // Sink callbacks arrive on the GPS thread – all UI work (snackbar,
        // reroute dialog flow) must run on the Blazor dispatcher.
        await InvokeAsync(async () =>
        {
            Snackbar.Add(string.Format(L["OffRouteDetected"], distanceMeters), Severity.Warning);
            await RerouteAsync(latitude, longitude);
        });
    }

    public async Task OnRouteUpdatedAsync(RouteResponse response)
    {
        if (_disposed) return;
        _currentRouteResponse = response;
        // Sink callbacks arrive on the GPS thread; ShowRouteOnMap touches
        // MarkersList and calls StateHasChanged, so it must run on the
        // Blazor dispatcher. Without this the reroute map update throws
        // and the route disappears from the map.
        await InvokeAsync(async () => await ShowRouteOnMap(response));

        // Road closure check for the (re)routed path – fire and forget, the
        // service reports via snackbar/pins on the dispatcher itself.
        _ = CheckClosuresForRouteAsync(response);
    }

    #endregion
    
    #region "Map Marker Helpers"
    
    private async Task UpdatePositionMarkerAsync(GpsReading reading, bool force = false)
    {
        if (_disposed || _map == null) return;
        if (Interlocked.CompareExchange(ref _markerUpdating, 1, 0) != 0) return;

        try
        {
            double lon = reading.Position.Longitude;
            double lat = reading.Position.Latitude;
            OpenLayers.Blazor.Coordinate coord = new OpenLayers.Blazor.Coordinate(lon, lat);

            if (!_gpsMarkerInitialized)
            {
                _map.MarkersList.Add(new Marker { Coordinate = coord, Type = MarkerType.MarkerPin, PinColor = PinColor.Green });
                _gpsMarkerInitialized = true;
            }
            else if (!force && (DateTime.UtcNow - _lastMarkerUpdate).TotalSeconds < 2 && 
                     GeoMath.DistanceMeters(_lastMarkerLat, _lastMarkerLon, lat, lon) < 1.0)
            {
                return;
            }

            _lastMarkerUpdate = DateTime.UtcNow;
            _lastMarkerLon = lon;
            _lastMarkerLat = lat;

            if (_map.MarkersList.Count > 0)
            {
                Marker old = (Marker)_map.MarkersList[0];
                _map.MarkersList[0] = new Marker { Coordinate = coord, Type = old.Type, PinColor = PinColor.Green, Text = "➤" };
            }

            await UpdateBreadcrumbAsync(force: force);

            // During navigation, follow the position: keep the map centered on
            // the current fix and rotate towards the travel direction, like a
            // navigation app. Throttled – see FollowNavigationViewAsync.
            if (NavService.IsNavigating)
            {
                await FollowNavigationViewAsync(reading);
            }

        }
        finally
        {
            Interlocked.Exchange(ref _markerUpdating, 0);
        }
    }

    // ── Navigation follow view: map tracks the rider ─────────────────────

    private const double NavFollowIntervalSec = 1.0;
    private const double NavFollowMinMoveM = 10.0;
    private const double NavFollowMinHeadingDelta = 8.0; // degrees

    /// <summary>
    /// Centers the map on the current GPS fix and rotates it towards the
    /// travel direction while navigating. Throttled to ~1 Hz and only when
    /// the position moved enough, so the map does not jitter on every fix.
    /// </summary>
    private async Task FollowNavigationViewAsync(GpsReading reading)
    {
        OpenStreetMap? map = _map;
        if (map == null) return;

        double lat = reading.Position.Latitude;
        double lon = reading.Position.Longitude;
        DateTime now = DateTime.UtcNow;

        bool movedEnough = _lastNavFollowUpdate == DateTime.MinValue ||
                           GeoMath.DistanceMeters(_lastFollowLat, _lastFollowLon, lat, lon) > NavFollowMinMoveM;
        if (!movedEnough || (now - _lastNavFollowUpdate).TotalSeconds < NavFollowIntervalSec)
            return;

        _lastNavFollowUpdate = now;
        _lastFollowLat = lat;
        _lastFollowLon = lon;

        await map.SetCenter(new OpenLayers.Blazor.Coordinate(lon, lat));

        // Heading while moving comes from the NavigationService (computed
        // travel direction: GPS course or buffer-derived) – the service owns
        // the logic and also uses it for reroute requests. The route bearing
        // is only a last resort here, e.g. before the first status arrives.
        double heading = _navStatus?.Heading > 0 ? _navStatus!.Heading : 0;
        if (heading <= 0)
        {
            double? routeBearing = BearingToNextRoutePoint(lat, lon);
            if (routeBearing is { } rb)
                heading = rb;
        }

        // Only rotate when the heading changed notably – avoids map wobble
        // on small GPS course fluctuations.
        if (heading > 0 && Math.Abs(heading - _lastFollowHeading) > NavFollowMinHeadingDelta)
        {
            map.Rotation = GeoMath.ToRad(heading);
            _lastFollowHeading = heading;
        }
    }

    private async Task ClearGpsMarkersAsync()
    {
        OpenStreetMap? map = _map;
        if (map == null) return;

        map.MarkersList.Clear();
        _gpsMarkerInitialized = false;

        // Waypoint pins were cleared too – restore them (planning state after navigation exit).
        await RefreshWaypointMarkersAsync(force: true);

        // Also remove breadcrumb layer if present
        Layer? breadcrumbLayer = map.LayersList.FirstOrDefault(l => l.Id == BreadcrumbLayerId);
        if (breadcrumbLayer != null)
            await map.RemoveLayer(breadcrumbLayer);
    }

    private async Task UpdateBreadcrumbAsync(bool force = false)
    {
        if (_disposed || Gps.Breadcrumb.Count < 2) return;
        OpenStreetMap? map = _map;
        if (map == null) return;

        DateTime now = DateTime.UtcNow;
        if (!force && (now - _lastBreadcrumbUpdate).TotalSeconds < 10)
            return;
        _lastBreadcrumbUpdate = now;

        if (Interlocked.CompareExchange(ref _breadcrumbUpdating, 1, 0) != 0) return;

        try
        {
            Layer? existing = map.LayersList.FirstOrDefault(l => l.Id == BreadcrumbLayerId);
            if (existing != null)
            {
                await map.RemoveLayer(existing);
            }

            List<Coordinate> coords = Gps.Breadcrumb
                .Select(r => new Coordinate(r.Position.Latitude, r.Position.Longitude, null))
                .ToList();
            string polyline6 = PolylineEncoder.EncodePolyline6(coords);
            if (string.IsNullOrEmpty(polyline6)) return;

            Layer layer = new()
            {
                Id = BreadcrumbLayerId,
                LayerType = LayerType.Vector,
                SourceType = SourceType.VectorPolyline,
                Projection = "EPSG:4326",
                Data = polyline6,
                FormatOptions = new { factor = 1e6 },
                Style = new StyleOptions
                {
                    Stroke = new StyleOptions.StrokeOptions
                    {
                        Color = "#2196F3", Width = 3, LineDash = [8, 4],
                        LineCap = "round", LineJoin = "round"
                    }
                }
            };

            if (_map != null)
            {
                await _map.AddLayer(layer);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _breadcrumbUpdating, 0);
        }
    }
    
    #endregion
    
    
    private async Task WaitForMapLoadedAsync(int timeoutMs = 5000)
    {
        using CancellationTokenSource cts = new(timeoutMs);
        try
        {
            while (!_mapLoaded)
            {
                await Task.Delay(50, cts.Token);
            }
        }
        catch (TaskCanceledException)
        {
            throw new TimeoutException("The map failed to render within the expected timeframe.");
        }
    }
    private List<GeoResult> GetPinnedStartLocations()
    {
        List<GeoResult> items = [];

        // Aktuelle Position immer anzeigen (auch ohne GPS-Fix)
        if (Gps.LastReading != null)
        {
            items.Add(new GeoResult(L["CurrentPos"],
                Gps.LastReading.Position.Latitude,
                Gps.LastReading.Position.Longitude, "current"));
        }
        else
        {
            // Placeholder – live GPS is fetched at start
            items.Add(new GeoResult(L["CurrentPos"], 0, 0, "current"));
        }

        string homeLabel = Preferences.Get("home_label", "");
        double homeLat = Preferences.Get("home_lat", 0.0);
        double homeLon = Preferences.Get("home_lon", 0.0);
        if (!string.IsNullOrEmpty(homeLabel) && homeLat != 0 && homeLon != 0)
            items.Add(new GeoResult(homeLabel, homeLat, homeLon, "home"));

        return items;
    }

    private async Task<IEnumerable<GeoResult>> SearchAsync(string? query, CancellationToken ct)
    {
        // Pins (Current Position / Home) are rendered via BeforeItemsTemplate/NoItemsTemplate.
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return [];

        // More results than shown, so the spatial bias has candidates to work with.

        // Bias Photon towards the current viewport (fallback: GPS position) so
        // local results are returned and ranked first (global queries like
        // "Mc Donalds" otherwise surface US/other-country matches).
        Extent? extent = _map?.VisibleExtent;
        double? biasLon = null, biasLat = null;
        if (extent != null && extent.X2 > extent.X1 && extent.Y2 > extent.Y1)
        {
            biasLon = (extent.X1 + extent.X2) / 2;
            biasLat = (extent.Y1 + extent.Y2) / 2;
        }
        else if (Gps.LastReading != null)
        {
            biasLon = Gps.LastReading.Position.Longitude;
            biasLat = Gps.LastReading.Position.Latitude;
        }

        return await GeocodingService.SuggestAsync(query, limit: 15, lon: biasLon, lat: biasLat, ct: ct);
    }

    /// <summary>
    /// Selects a pinned location via the autocomplete – sets the value and closes the menu.
    /// </summary>
    private async Task SelectPinAsync(MudAutocomplete<GeoResult> auto, GeoResult pin)
    {
        if (auto == null) return;
        await auto.SelectOptionAsync(pin);
    }

    private static Location CreateLocation(GeoResult? location, string type = "break")
    {
        return new Location { Lat = location!.Latitude, Lon = location.Longitude, Type = type };
    }

    private const int MaxViaLocations = 48;
    private const double WaypointTapRadiusM = 30;

    // ── Route Calculation ──

    private async Task CalculateRoute()
    {
        try
        {

            if (_startLocation == null || _destinationLocation == null) return;

            _errorMessage = null;

            // Valhalla-limit: max ~50 locations total → max 48 via + start + destination
            if (_waypoints is { Count: > MaxViaLocations })
            {
                int originalCount = _waypoints.Count;
                ReduceWaypointsToMax(MaxViaLocations);
                Snackbar.Add(
                    string.Format(L["WaypointsReduced"], originalCount, _waypoints.Count, MaxViaLocations),
                    Severity.Warning);
            }

            _isLoading = true;
            StateHasChanged();

            // Start position: take fresh GPS data when "current position" is selected
            double startLat = 0, startLon = 0;
            if (_startLocation?.Type == "current")
            {
                await Gps.GetLastReadingOrCurrentAsync();
                if (Gps.LastReading != null)
                {
                    startLat = Gps.LastReading.Position.Latitude;
                    startLon = Gps.LastReading.Position.Longitude;
                }
            }
            else if (_startLocation != null)
            {
                startLat = _startLocation.Latitude;
                startLon = _startLocation.Longitude;
            }

            if (startLat == 0 && startLon == 0)
            {
                throw new Exception("StartLocationNotDetermined");
            }

            List<Location> locs = [new() { Lat = startLat, Lon = startLon, Type = "break" }];

            if (_waypoints is { Count: > 0 })
            {
                locs.AddRange(
                    from waypoint in _waypoints
                    where waypoint.Location != null
                    select CreateLocation(waypoint.Location, "break"));
            }

            locs.Add(CreateLocation(_destinationLocation));

            RouteRequest request = new()
            {
                Locations = locs,
                Costing = "motorcycle",
                DirectionsOptions = new DirectionsOptions { Units = "kilometers", Language = "de" }
            };

            Result result = await ValhallaClient.GetRouteAsync(request);

            if (result is { IsSuccess: true, Response: not null })
            {
                _currentRouteResponse = result.Response;
                await ShowRouteOnMap(result.Response);
                _ = CheckClosuresForRouteAsync(result.Response);
            }
            else
            {
                _currentRouteResponse = null;
                _errorMessage = result.ErrorMessage ?? "Unbekannter Fehler bei der Routenberechnung";
            }
        }
        catch (Exception e)
        {
            Snackbar.Add("Error: " + e.Message, Severity.Error);
        }
        finally
        {
            // The closure check (started above) is part of "calculating the
            // route" from the user's perspective – keep the loading state up
            // until it finishes. CheckClosuresForRouteAsync releases it.
            if (!_closureCheckRunning)
            {
                _isLoading = false;
                StateHasChanged();
            }
        }
    }

    private void ReduceWaypointsToMax(int maxCount)
    {
        if (_waypoints.Count <= maxCount) return;

        // Keep first, last, and sample the middle
        WaypointViewModel first = _waypoints[0];
        WaypointViewModel last = _waypoints[^1];
        List<WaypointViewModel> middle = _waypoints.Skip(1).Take(_waypoints.Count - 2).ToList();

        // Simple sampling: take every Nth point
        int step = (int)Math.Ceiling((double)middle.Count / (maxCount - 2));
        List<WaypointViewModel> sampled = middle.Where((_, index) => index % step == 0).Take(maxCount - 2).ToList();

        _waypoints = [first, .. sampled, last];
    }

    private async Task SaveCurrentRoute()
    {
        if (_currentRouteResponse?.Trip?.Legs == null || _currentRouteResponse.Trip.Legs.Count == 0) return;

        // Already saved this session (or loaded via URL) → update, no name dialog.
        string existingId = RouteId ?? _savedRouteId ?? "";
        SavedRoute? route = !string.IsNullOrWhiteSpace(existingId)
            ? await RouteStorage.GetRouteByIdAsync(existingId)
            : null;

        string? routeName = route?.Name;
        if (string.IsNullOrWhiteSpace(routeName))
        {
            // New route: get name from dialog
            DialogParameters<RenameDialog> parameters = new()
            {
                { nameof(RenameDialog.ContentText), (string)L["SaveDialogPrompt"] },
                { nameof(RenameDialog.ButtonText), (string)L["Save"] },
                { "Color", MudBlazor.Color.Primary }
            };
            IDialogReference dialog = await DialogService.ShowAsync<RenameDialog>(L["SaveDialogTitle"], parameters);
            DialogResult? result = await dialog.Result;
            if (result is null || result.Canceled || result.Data is not string name || string.IsNullOrWhiteSpace(name))
                return;
            routeName = name;
        }

        route ??= new SavedRoute();

        _isSaving = true;
        try
        {
            (string combinedShape, _, double totalDistanceKm, double totalTimeMinutes) =
                NavService.PrepareNavigationData(_currentRouteResponse!.Trip!.Legs);

            route.Name = routeName;
            route.Polyline6 = combinedShape;
            route.DistanceKm = Math.Round(totalDistanceKm, 2);
            route.TimeMinutes = Math.Round(totalTimeMinutes, 2);
            route.Waypoints = [];

            foreach (Location wp in _currentRouteResponse.Trip!.Locations!)
            {
                List<GeoResult> reverseGeo = await GeocodingService.GetReverseGeocodingAsync(wp.Lon, wp.Lat);
                route.Waypoints.Add(new Coordinate(wp.Lat, wp.Lon, reverseGeo.FirstOrDefault()?.Label ?? "Waypoint"));
            }

            await RouteStorage.SaveRouteAsync(route);
            _savedRouteId = route.Id;
            Snackbar.Add(L["RouteSaved"], Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add(string.Format(L["RouteSaveError"], ex.Message), Severity.Error);
        }
        finally { _isSaving = false; }
    }

    // ── Start Navigation ────────────────────────────────────────────────

    private async Task StartNavigation()
    {
        if (Gps.LastReading == null)
        {
            await Gps.GetLastReadingOrCurrentAsync();
            if (Gps.LastReading == null)
            {
                Snackbar.Add(L["NoGPS"], Severity.Error);
                return;
            }
        }

        // Always start from current position to avoid "planning from far away" bug
        _startLocation = new GeoResult(L["CurrentPos"], Gps.LastReading!.Position.Latitude, Gps.LastReading!.Position.Longitude, "current");
        await CalculateRoute();

        if (_currentRouteResponse?.Trip?.Legs == null || _currentRouteResponse.Trip.Legs.Count == 0)
        {
            Snackbar.Add(L["NoRoute"], Severity.Error);
            return;
        }

        try
        {
            (string mergedShape, List<Maneuver> allManeuvers, double totalKm, double totalMin) =
                NavService.PrepareNavigationData(_currentRouteResponse.Trip.Legs);

            // Pass the intermediate waypoints to the navigation service so a
            // reroute (off-route or skip-waypoint) keeps driving through the
            // remaining waypoints instead of dropping them all.
            List<Models.Valhalla.Location> viaLocations = _waypoints
                .Where(w => w.Location != null)
                .Select(w => CreateLocation(w.Location!, "break"))
                .ToList();

            await NavService.StartNavigation(
                mergedShape, allManeuvers, totalKm, totalMin,
                CreateLocation(_destinationLocation), viaLocations);
            Snackbar.Add(L["NavigationStarted"], Severity.Success);

            // Nav-app behavior: zoom in on the current position and rotate
            // the map so the travel direction points up.
            await EnterNavigationViewAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add(string.Format(L["NavigationError"], ex.Message), Severity.Error);
        }
    }

    // ── Navigation view: zoom in + rotate towards travel direction ──────

    private const double NavigationViewZoom = 16;

    private async Task EnterNavigationViewAsync()
    {
        OpenStreetMap? map = _map;
        GpsReading? reading = Gps.LastReading;
        if (map == null || reading == null) return;

        double lat = reading.Position.Latitude;
        double lon = reading.Position.Longitude;

        await map.SetCenter(new OpenLayers.Blazor.Coordinate(lon, lat));
        await map.SetZoom(NavigationViewZoom);

        // Heading at start: the service-computed travel direction if already
        // available, otherwise the bearing towards the next route point
        // (still standing at the start – the route is the best guess for the
        // upcoming direction).
        double heading = _navStatus?.Heading > 0 ? _navStatus!.Heading : 0;
        if (heading <= 0)
        {
            double? routeBearing = BearingToNextRoutePoint(lat, lon);
            if (routeBearing is { } bearing)
                heading = bearing;
        }

        if (heading > 0)
        {
            // OpenLayers: positive rotation = clockwise, radians.
            map.Rotation = GeoMath.ToRad(heading);
        }
    }

    /// <summary>
    /// Bearing from the given position towards the first route point that is
    /// at least ~30 m ahead. Returns null when no route is loaded.
    /// </summary>
    private double? BearingToNextRoutePoint(double lat, double lon)
    {
        if (_currentRouteResponse?.Trip?.Legs is not { Count: > 0 } legs) return null;

        List<Coordinate> coords = PolylineEncoder.DecodePolyline6(legs[0].Shape);
        foreach (Coordinate c in coords)
        {
            if (GeoMath.DistanceMeters(lat, lon, c.Latitude, c.Longitude) < 30)
                continue;

            // Initial bearing (great-circle) from position to the route point.
            double phi1 = GeoMath.ToRad(lat);
            double phi2 = GeoMath.ToRad(c.Latitude);
            double dLon = GeoMath.ToRad(c.Longitude - lon);
            double y = Math.Sin(dLon) * Math.Cos(phi2);
            double x = Math.Cos(phi1) * Math.Sin(phi2) -
                       Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLon);
            double bearingDeg = (GeoMath.ToDeg(Math.Atan2(y, x)) + 360.0) % 360.0;
            return bearingDeg;
        }
        return null;
    }

    private async Task ExitNavigation()
    {
        await NavService.StopNavigation();
        // Skipped waypoints were only skipped for the running navigation –
        // the planned route (list + pins) stays intact after stopping.
        _skippedWaypointIds.Clear();
        await ClearGpsMarkersAsync();
        if (_map != null)
        {
            // Back to north-up view after navigation.
            _map.Rotation = 0;
        }

        // Reset follow-view state so the next navigation starts fresh.
        _lastNavFollowUpdate = DateTime.MinValue;
        _lastFollowHeading = -1;
        Snackbar.Add(L["NavigationStopped"], Severity.Info);
    }

    private async Task SkipWaypoint()
    {
        if (_isLoading)
        {
            Snackbar.Add(L["WaypointSkipInProgress"], Severity.Info);
            return;
        }

        if (Gps.LastReading == null)
        {
            Snackbar.Add(L["WaypointSkipNoGPS"], Severity.Warning);
            return;
        }
        
        double lat = Gps.LastReading.Position.Latitude;
        double lon = Gps.LastReading.Position.Longitude;
        Snackbar.Add(L["WaypointSkip"], Severity.Warning);
        // skipNextWaypoint=true: the service drops the first not-yet-reached
        // waypoint and reroutes through all remaining ones.
        await RerouteAsync(lat, lon, skipNextWaypoint: true);
    }

    private async Task RerouteAsync(double currentLat, double currentLon, bool skipNextWaypoint = false)
    {
        if (_isLoading) return;
        _isLoading = true;

        try
        {
            // Reroute calculation lives in the NavigationService (state + sink notify).
            // Map update happens via OnRouteUpdatedAsync.
            (bool rerouted, int skippedViaIndex) = await NavService.PerformRerouteAsync(
                currentLat, currentLon, skipNextWaypoint);

            if (rerouted && skippedViaIndex >= 0)
            {
                // skippedViaIndex refers to the waypoints WITH a location
                // minus those already skipped (same filtered list the
                // service works with). Map it back to the raw _waypoints
                // list, which may contain empty entries.
                List<WaypointViewModel> skippable = _waypoints
                    .Where(w => w.Location != null && !_skippedWaypointIds.Contains(w.Id))
                    .ToList();
                if (skippedViaIndex < skippable.Count)
                {
                    // Mark as skipped instead of removing: the waypoint must
                    // still be there when navigation ends (planning list +
                    // pins). Only the running navigation drops it.
                    _skippedWaypointIds.Add(skippable[skippedViaIndex].Id);
                    await RefreshWaypointMarkersAsync(force: true);
                }
            }

            Snackbar.Add(
                rerouted ? L["RouteRecalculated"] : L["RerouteNoRoute"],
                rerouted ? Severity.Success : Severity.Error);
        }
        catch (Exception ex)
        {
            Snackbar.Add(string.Format(L["RerouteError"], ex.Message), Severity.Error);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private string GetTurnIcon(int valhallaType)
    {
        string iconKey = NavigationIconMapper.GetSemanticIcon(valhallaType);
        return iconKey switch
        {
            NavigationIconMapper.IconTurnLeft => Icons.Material.Filled.TurnLeft,
            NavigationIconMapper.IconTurnRight => Icons.Material.Filled.TurnRight,
            NavigationIconMapper.IconSlightLeft => Icons.Material.Filled.TurnSlightLeft,
            NavigationIconMapper.IconSlightRight => Icons.Material.Filled.TurnSlightRight,
            NavigationIconMapper.IconSharpLeft => Icons.Material.Filled.TurnSharpLeft,
            NavigationIconMapper.IconSharpRight => Icons.Material.Filled.TurnSharpRight,
            NavigationIconMapper.IconUTurn => Icons.Material.Filled.UTurnLeft,
            NavigationIconMapper.IconStraight => Icons.Material.Filled.Straight,
            NavigationIconMapper.IconFinish => Icons.Material.Filled.Flag,
            NavigationIconMapper.IconRoundabout => Icons.Material.Filled.RotateRight,
            _ => Icons.Material.Filled.Straight
        };
    }

    private string GetTurnLabel(int valhallaType)
    {
        string iconKey = NavigationIconMapper.GetSemanticIcon(valhallaType);
        return iconKey switch
        {
            NavigationIconMapper.IconTurnLeft => L["TurnLeft"],
            NavigationIconMapper.IconTurnRight => L["TurnRight"],
            NavigationIconMapper.IconSlightLeft => L["TurnSlightLeft"],
            NavigationIconMapper.IconSlightRight => L["TurnSlightRight"],
            NavigationIconMapper.IconSharpLeft => L["TurnSharpLeft"],
            NavigationIconMapper.IconSharpRight => L["TurnSharpRight"],
            NavigationIconMapper.IconUTurn => L["UTurn"],
            NavigationIconMapper.IconStraight => L["Straight"],
            NavigationIconMapper.IconFinish => L["Arrival"],
            NavigationIconMapper.IconRoundabout => L["Roundabout"],
            _ => L["Straight"]
        };
    }

    private string FormatTime(double? minutes)
    {
        switch (minutes)
        {
            case null:
            case < 0:
                return "–";
            case < 1:
                return L["NavLessThanMinute"];
            case < 60:
                return string.Format(L["NavMin"], (int)minutes.Value);
        }

        int hours = (int)(minutes.Value / 60);
        int mins = (int)(minutes.Value % 60);
        return string.Format(L["NavHoursMinutes"], hours, mins);
    }

    private async Task ShowRouteOnMapFromPolyline(string polyline6)
    {
        OpenStreetMap? map = _map;
        if (map == null) return;

        // 2. Alte Route-Layer entfernen
        Layer? existingLayer = map.LayersList.FirstOrDefault(l => l.Id == RouteLayerId);
        if (existingLayer != null)
            await map.RemoveLayer(existingLayer);

        // 3. Add a new layer with the vector polyline
        Layer layer = new()
        {
            Id = RouteLayerId,
            LayerType = LayerType.Vector,
            SourceType = SourceType.VectorPolyline,
            Projection = "EPSG:4326",
            Data = polyline6,
            FormatOptions = new { factor = 1e6 },
            Style = new StyleOptions
            {
                Stroke = new StyleOptions.StrokeOptions
                {
                    Color = "#FF5722",
                    Width = 5,
                    LineCap = "round",
                    LineJoin = "round"
                }
            }
        };

        await map.AddLayer(layer);
        await UpdateBreadcrumbAsync(force: true);
    }
    private async Task ShowRouteOnMap(RouteResponse response)
    {
        if (response.Trip?.Legs == null) return;
        OpenStreetMap? map = _map;
        if (map == null) return;

        (string combinedShape, _, _, _) = NavService.PrepareNavigationData(response.Trip.Legs);

        if (string.IsNullOrEmpty(combinedShape))
        {
            _errorMessage = L["RouteNoGeometry"];
            return;
        }

        await ShowRouteOnMapFromPolyline(combinedShape);

        // ── Markers for Start, Waypoints and Destination ──
        if (response.Trip.Locations is { Count: > 0 })
        {
            // Keep the GPS position marker (index 0), clear all others so old
            // route pins are removed, then rebuild the route pins below.
            // NOTE: the pin rebuild must NOT be gated on MarkersList.Count –
            // when only the GPS marker exists (Count == 1) the route pins
            // would silently never be drawn.
            List<Shape> gpsMarkers = [.. map.MarkersList.Take(1)];
            map.MarkersList.Clear();
            StateHasChanged();
            foreach (Shape m in gpsMarkers) map.MarkersList.Add(m);

            List<Location> locations = response.Trip.Locations;

            // Trip order matches CalculateRoute: non-null waypoints only.
            List<WaypointViewModel> routeWaypoints = _waypoints
                .Where(w => w.Location != null && !_skippedWaypointIds.Contains(w.Id))
                .ToList();
            _waypointPins.Clear();

            for (int i = 0; i < locations.Count; i++)
            {
                Location loc = locations[i];
                OpenLayers.Blazor.Coordinate coord = new(loc.Lon, loc.Lat);

                // Color logic: Green start, blue waypoints, red destination.
                PinColor color = i switch
                {
                    0 => PinColor.Green,
                    _ when i == locations.Count - 1 => PinColor.Red,
                    _ => PinColor.Blue
                };

                Marker marker = new(MarkerType.MarkerPin, coord, "", color);
                map.MarkersList.Add(marker);

                // Middle locations are waypoints – keep them tappable (move/delete).
                if (i > 0 && i < locations.Count - 1 && i - 1 < routeWaypoints.Count)
                {
                    _waypointPins.Add((routeWaypoints[i - 1], marker));
                }
            }
        }

        Summary? summary = response.Trip?.Summary;
        // During navigation the follow-view controls center/zoom/rotation –
        // ShowRouteOnMap must NOT touch the view, or the reroute kills the
        // zoom/heading the rider is relying on.
        if (!NavService.IsNavigating && summary is { MinLat: not null, MaxLat: not null, MinLon: not null, MaxLon: not null })
        {
            if (_savedZoom is { } zoom && _hasShownRoute)
            {
                // User has zoomed in before: keep their view instead of
                // resetting to the route extent on every (re)calculation.
                await map.SetZoom(zoom);
            }
            else
            {
                // First display of a route: fit the map to the route extent.
                Extent extent = new(
                    summary.MinLon.Value, summary.MinLat.Value,
                    summary.MaxLon.Value, summary.MaxLat.Value);

                await map.SetVisibleExtent(extent);
                _hasShownRoute = true;
            }
        }

        // Yellow overlay + pins for known closures. Decode the route shape
        // (already computed above) to position the highlight window.
        if (string.IsNullOrEmpty(combinedShape))
            await ReAddClosureMarkersAsync(map, null);
        else
            await ReAddClosureMarkersAsync(map, PolylineEncoder.DecodePolyline6(combinedShape));
    }

    // ── Road Closure Check (Overpass) ────────────────────────────────────

    private async Task CheckClosuresForRouteAsync(RouteResponse response)
    {
        if (_disposed) return;
        if (_closureCheckInFlight) return;
        if ((DateTime.UtcNow - _lastClosureCheckUtc).TotalSeconds < 60) return;
        _lastClosureCheckUtc = DateTime.UtcNow;
        _closureCheckInFlight = true;
        _closureCheckRunning = true;
        await InvokeAsync(() =>
        {
            // Make the background work visible: the map indicator stays up
            // for the whole check and the snackbar tells the user data is
            // being fetched (first request downloads the MobiData feed).
            Snackbar.Add(L["ClosureCheckRunning"], Severity.Info);
            StateHasChanged();
        });

        try
        {
            // Decode the route geometry so the service can build the corridor.
            (string mergedShape, _, _, _) = NavService.PrepareNavigationData(response.Trip?.Legs ?? []);
            List<Coordinate> routeCoords = PolylineEncoder.DecodePolyline6(mergedShape);
            if (routeCoords.Count < 2) return;

            RoadClosureCheckResult result = await RoadClosureService.CheckRouteAsync(routeCoords);

            await InvokeAsync(async () =>
            {
                _closures.Clear();

                if (!result.IsSuccess)
                {
                    Log.Warning("Road closure check unavailable: {Reason}", result.ErrorMessage);
                    return;
                }

                _closures.AddRange(result.Closures);
                await ReAddClosureMarkersAsync(_map, routeCoords);

                if (_closures.Count > 0)
                {
                    Snackbar.Add(
                        string.Format(L["ClosuresFound"], _closures.Count),
                        Severity.Warning);
                }

                StateHasChanged();
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Road closure check failed");
        }
        finally
        {
            _closureCheckInFlight = false;
            _closureCheckRunning = false;
            // Release the loading state held by CalculateRoute (see there) –
            // the closure check was the last step of "calculating the route".
            _isLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// Adds one red pin per known closure to the map plus a yellow overlay
    /// along the route where the closure lies. Call whenever the marker list
    /// was rebuilt (new route shown, markers cleared).
    /// </summary>
    private async Task ReAddClosureMarkersAsync(OpenStreetMap? map, List<Coordinate>? routeCoords)
    {
        if (map == null) return;

        // 1. Red pins at the closure positions.
        foreach (RoadClosure closure in _closures)
        {
            map.MarkersList.Add(new Marker(
                MarkerType.MarkerFlag,
                new OpenLayers.Blazor.Coordinate(closure.Longitude, closure.Latitude),
                "⚠",
                PinColor.Red));
        }

        // 2. Yellow overlay: highlight the route segment around each closure
        //    so it is visible at a glance (the user asked for a yellow route
        //    stroke instead of / in addition to the pins).
        if (_closures.Count == 0 || routeCoords is not { Count: > 1 }) return;

        // Remove any previous closure overlay layers.
        List<Layer> oldOverlays = map.LayersList
            .Where(l => l.Id?.StartsWith(ClosureLayerIdPrefix) == true)
            .ToList();
        foreach (Layer l in oldOverlays)
            await map.RemoveLayer(l);

        foreach (RoadClosure closure in _closures)
        {
            // Find the route point nearest to the closure position and take a
            // window around it (half the corridor each side).
            int nearestIdx = FindNearestRoutePointIndex(closure.Latitude, closure.Longitude, routeCoords);
            if (nearestIdx < 0) continue;

            List<Coordinate> segment = ExtractRouteWindow(routeCoords, nearestIdx, ClosureHighlightRadiusM);
            if (segment.Count < 2) continue;

            string polyline = PolylineEncoder.EncodePolyline6(segment);
            Layer overlay = new()
            {
                Id = $"{ClosureLayerIdPrefix}{closure.OsmId}",
                LayerType = LayerType.Vector,
                SourceType = SourceType.VectorPolyline,
                Projection = "EPSG:4326",
                Data = polyline,
                FormatOptions = new { factor = 1e6 },
                Style = new StyleOptions
                {
                    Stroke = new StyleOptions.StrokeOptions
                    {
                        Color = ClosureHighlightColor,
                        Width = 8,
                        LineCap = "round",
                        LineJoin = "round"
                    }
                }
            };
            await map.AddLayer(overlay);
        }
    }

    /// <summary>Index of the route point closest to the given position, or -1.</summary>
    private static int FindNearestRoutePointIndex(double lat, double lon, List<Coordinate> routeCoords)
    {
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < routeCoords.Count; i++)
        {
            double d = GeoMath.DistanceMeters(lat, lon, routeCoords[i].Latitude, routeCoords[i].Longitude);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// Extracts a window of route points around <paramref name="centerIdx"/>,
    /// extending roughly <paramref name="radiusM"/> meters in both directions.
    /// </summary>
    private static List<Coordinate> ExtractRouteWindow(
        List<Coordinate> routeCoords, int centerIdx, double radiusM)
    {
        List<Coordinate> result = [routeCoords[centerIdx]];

        // Forward from the center.
        double dist = 0;
        for (int i = centerIdx + 1; i < routeCoords.Count && dist < radiusM; i++)
        {
            dist += GeoMath.DistanceMeters(
                routeCoords[i - 1].Latitude, routeCoords[i - 1].Longitude,
                routeCoords[i].Latitude, routeCoords[i].Longitude);
            result.Add(routeCoords[i]);
        }

        // Backward from the center (prepend).
        List<Coordinate> backward = [];
        dist = 0;
        for (int i = centerIdx - 1; i >= 0 && dist < radiusM; i--)
        {
            dist += GeoMath.DistanceMeters(
                routeCoords[i + 1].Latitude, routeCoords[i + 1].Longitude,
                routeCoords[i].Latitude, routeCoords[i].Longitude);
            backward.Add(routeCoords[i]);
        }
        backward.Reverse();
        backward.AddRange(result);
        return backward;
    }

    private static string FormatCoordinate(double? lat, double? lon)
    {
        if (lat == null || lon == null) return "–";
        return $"{lat.Value:F5}, {lon.Value:F5}";
    }

    private async Task GetMyCurrentPosition()
    {
        OpenStreetMap? map = _map;
        if (map == null) return;

        await Gps.GetLastReadingOrCurrentAsync();
        
        if (Gps.LastReading != null)
        {
            GpsReading reading = Gps.LastReading;
            OpenLayers.Blazor.Coordinate coord = new(
                reading.Position.Longitude,
                reading.Position.Latitude);
            
            await map.SetCenter(coord);
            await map.SetZoom(8);
            
            // Force marker update to bypass the 2s throttle in UpdatePositionMarkerAsync
            await UpdatePositionMarkerAsync(reading, force: true);
        }
        else
        {
            // No GPS fix yet – keep the initial zoom level (the Zoom="8"
            // markup parameter was removed so re-renders cannot reset the
            // view during navigation; the default comes from here instead).
            await map.SetZoom(8);
            Snackbar.Add(L["NoGpsFix"], Severity.Info);
        }
    }

    // ── IAsyncDisposable ─────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        // Zuerst Events unsubscriben – keine neuen Callbacks mehr
        Gps.ReadingReceived -= OnGpsReading;
        Gps.TrackingChanged -= OnGpsTrackingChanged;

            NavService.RemoveSink(this);

        // Clear the map reference – the JS object may no longer exist
        _map = null;

        if (Gps.IsTracking)
        {
            await Gps.StopTrackingAsync();
        }
    }
}
