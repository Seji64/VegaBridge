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

    private OpenStreetMap? _map;
    private MudAutocomplete<GeoResult> _startAuto = null!;
    private MudAutocomplete<GeoResult> _destAuto = null!;

    private GeoResult? _startLocation;
    private GeoResult? _destinationLocation;
    private List<WaypointViewModel> _waypoints = [];
    private readonly List<(WaypointViewModel Waypoint, Marker Marker)> _waypointPins = [];
    private WaypointViewModel? _pendingMoveWaypoint;
    private DateTime _lastMarkerClickUtc = DateTime.MinValue;
    private bool _actionsDialogOpen;
    private string? _errorMessage;
    private bool _isLoading;
    private bool _isSaving;
    private RouteResponse? _currentRouteResponse;
    private bool _mapLoaded = false;

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

                    // Automatisch Route via Valhalla berechnen (für Navigation starten + Turn-by-Turn)
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
        if (result is { Canceled: false } && result.Data is string action)
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

        // Default-Start auf "Aktuelle Position" beim ersten GPS-Fix
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
        Snackbar.Add(L["DestinationReached"], Severity.Success);
        await InvokeAsync(StateHasChanged);
    }

    public async Task OnCancelAsync()
    {
        if (_disposed) return;
        Snackbar.Add(L["NavigationStopped"], Severity.Info);
        await InvokeAsync(StateHasChanged);
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
        Snackbar.Add(string.Format(L["OffRouteDetected"], distanceMeters), Severity.Warning);
        await RerouteAsync(latitude, longitude);
    }

    public async Task OnRouteUpdatedAsync(RouteResponse response)
    {
        if (_disposed) return;
        _currentRouteResponse = response;
        await ShowRouteOnMap(response);
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

        }
        finally
        {
            Interlocked.Exchange(ref _markerUpdating, 0);
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
        Layer? breadcrumbLayer = map.LayersList?
            .FirstOrDefault(l => l.Id == BreadcrumbLayerId);
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
            Layer? existing = map.LayersList?.FirstOrDefault(l => l.Id == BreadcrumbLayerId);
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
            // Platzhalter – beim Start wird live GPS geholt
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

        return await GeocodingService.SuggestAsync(query, ct: ct);
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

            // Start-Position: bei "Aktuelle Position" frische GPS-Daten nehmen
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
            _isLoading = false;
            StateHasChanged();
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
        List<WaypointViewModel> sampled = middle.Where((item, index) => index % step == 0).Take(maxCount - 2).ToList();

        _waypoints = [first, .. sampled, last];
    }

    private async Task SaveCurrentRoute()
    {
        if (_currentRouteResponse?.Trip?.Legs == null || _currentRouteResponse.Trip.Legs.Count == 0) return;

        string? routeName = RouteId;
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

        _isSaving = true;
        try
        {
            (string combinedShape, _, double totalDistanceKm, double totalTimeMinutes) = 
                NavService.PrepareNavigationData(_currentRouteResponse!.Trip!.Legs);

            SavedRoute? route = !string.IsNullOrWhiteSpace(RouteId) 
                ? await RouteStorage.GetRouteByIdAsync(RouteId) 
                : new SavedRoute();

            if (route == null)
            {
                Snackbar.Add(L["RouteLoadFailed"], Severity.Error);
                return;
            }

            route.Name = routeName;
            route.Polyline6 = combinedShape;
            route.DistanceKm = Math.Round(totalDistanceKm, 2);
            route.TimeMinutes = Math.Round(totalTimeMinutes, 2);
            route.Waypoints = [];

            foreach (Location wp in _currentRouteResponse.Trip!.Locations!)
            {
                List<GeoResult> reverseGeo = await GeocodingService.GetReverseGeocodingAsync(wp.Lon, wp.Lat);
                route.Waypoints.Add(new Models.Valhalla.Coordinate(wp.Lat, wp.Lon, reverseGeo.FirstOrDefault()?.Label ?? "Waypoint"));
            }

            await RouteStorage.SaveRouteAsync(route);
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
            await NavService.StartNavigation(mergedShape, allManeuvers, totalKm, totalMin, CreateLocation(_destinationLocation));
            Snackbar.Add(L["NavigationStarted"], Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add(string.Format(L["NavigationError"], ex.Message), Severity.Error);
        }
    }

    private async Task ExitNavigation()
    {
        await NavService.StopNavigation();
        await ClearGpsMarkersAsync();
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
        await RerouteAsync(lat, lon);
    }

    private async Task RerouteAsync(double currentLat, double currentLon)
    {
        if (_isLoading) return;
        _isLoading = true;

        try
        {
            // Reroute-Berechnung liegt im NavigationService (State + Sink-Notify).
            // Karten-Update erfolgt über OnRouteUpdatedAsync.
            bool rerouted = await NavService.PerformRerouteAsync(currentLat, currentLon);

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
        Layer? existingLayer = map.LayersList?.FirstOrDefault(l => l.Id == RouteLayerId);
        if (existingLayer != null)
            await map.RemoveLayer(existingLayer);

        // 3. Neuen Layer mit VectorPolyline hinzufügen
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
            // Keep the first two markers (GPS position and heading arrow)
            // Clear all markers from index 2 onwards to remove old route pins
            if (map.MarkersList.Count > 1)
            {
                List<Shape> gpsMarkers = [.. map.MarkersList.Take(1)];
                map.MarkersList.Clear();
                StateHasChanged();
                foreach (Shape m in gpsMarkers) map.MarkersList.Add(m);

                List<Location> locations = response.Trip.Locations;

                // Trip order matches CalculateRoute: non-null waypoints only.
                List<WaypointViewModel> routeWaypoints = _waypoints.Where(w => w.Location != null).ToList();
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
            if (summary is { MinLat: not null, MaxLat: not null, MinLon: not null, MaxLon: not null })
            {
                Extent extent = new(
                    summary.MinLon.Value, summary.MinLat.Value,
                    summary.MaxLon.Value, summary.MaxLat.Value);

                await map.SetVisibleExtent(extent);
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

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

        // Map-Referenz löschen – JS-Objekt existiert dann ggf. nicht mehr
        _map = null;

        if (Gps.IsTracking)
        {
            await Gps.StopTrackingAsync();
        }
    }
}
