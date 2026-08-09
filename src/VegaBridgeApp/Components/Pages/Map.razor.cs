using Microsoft.AspNetCore.Components;
using OpenLayers.Blazor;
using System.Runtime.CompilerServices;
using VegaBridgeApp.Models.Geocoding;
using VegaBridgeApp.Models.Valhalla;
using VegaBridgeApp.Models.Routes;
using VegaBridgeApp.Services.Routes;
using VegaBridgeApp.Models.Navigation;
using VegaBridgeApp.Components.Dialogs;
using MudBlazor;
using Shiny.Locations;
using VegaBridgeApp.Models.Utils;
using VegaBridgeApp.Utils;
using Coordinate = VegaBridgeApp.Models.Valhalla.Coordinate;
using Location = VegaBridgeApp.Models.Valhalla.Location;

namespace VegaBridgeApp.Components.Pages;

public partial class Map : ComponentBase, IAsyncDisposable
{
    [Parameter]
    public string? RouteId { get; set; }
    
    private const string RouteLayerId = "route-layer";
    private const string BreadcrumbLayerId = "breadcrumb-layer";

    private OpenStreetMap? _map;

    private GeoResult? _startLocation;
    private GeoResult? _destinationLocation;
    private List<WaypointViewModel> _waypoints = [];
    private string? _errorMessage;
    private bool _isLoading;
    private bool _isSaving;
    private RouteResponse? _currentRouteResponse;
    private bool _mapLoaded;

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
    private bool _hasLastMarkerCoord;

    // ── Navigation state ──
    private NavigationManeuverInfo? _navManeuver;
    private NavigationStatus? _navStatus;
    private double _navProgress;
    private bool _isNavigating;

    protected override void OnInitialized()
    {
        // Subscribe to GPS position updates
        Gps.ReadingReceived += OnGpsReading;
        Gps.TrackingChanged += OnGpsTrackingChanged;

        // Subscribe to navigation events
        NavService.ManeuverChanged += OnManeuverChanged;
        NavService.StatusUpdated += OnStatusUpdated;
        NavService.NavigationCompleted += OnNavigationCompleted;
        NavService.NavigationStateChanged += OnNavigationStateChanged;
        NavService.OffRouteDetected += OnOffRouteDetected;
    }

    private void AddWaypoint()
    {
        _waypoints.Add(new WaypointViewModel());
        StateHasChanged();
    }

    // ── Waypoint-Helpers ──

    private void RemoveWaypoint(int index)
    {
        if (index < 0 || index >= _waypoints.Count) return;
        _waypoints.RemoveAt(index);
        StateHasChanged();
    }

    private void MoveWaypoint(WaypointViewModel waypoint, int direction)
    {
        int index = _waypoints.IndexOf(waypoint);
        int newIndex = index + direction;

        if (newIndex < 0 || newIndex >= _waypoints.Count) return;
        _waypoints.RemoveAt(index);
        _waypoints.Insert(newIndex, waypoint);
        StateHasChanged();
    }

    // ── GPS Tracking ─────────────────────────────────────────────────────

    private async Task ToggleGpsTracking()
    {
        // Don't allow manual GPS toggle during active navigation
        if (_isNavigating)
        {
            Snackbar.Add(L["GpsManagedByNavigation"], Severity.Info);
            return;
        }

        try
        {
            if (Gps.IsTracking)
            {
                Gps.ClearBreadcrumb();
                await Gps.StopTrackingAsync();
                await ClearGpsMarkersAsync();
                Snackbar.Add(L["GPSStopped"], Severity.Info);
            }
            else
            {
                await Gps.StartTrackingAsync(backgroundMode: true);
                Snackbar.Add(L["GPSStarted"], Severity.Success);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add(string.Format(L["GPSError"], ex.Message), Severity.Error);
        }
    }

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
        if (_map != null && _mapLoaded && !_disposed)
            await UpdatePositionMarkerAsync(reading);

        // Throttled UI refresh: re-render at most every 500ms to avoid map re-creation
        if (_disposed) return;
        DateTime now = DateTime.UtcNow;
        if ((now - _lastUiRefresh).TotalMilliseconds >= 500)
        {
            _lastUiRefresh = now;
            try { await InvokeAsync(StateHasChanged); } catch { /* component disposed */ }
        }
    }

    private async void OnGpsTrackingChanged(bool isTracking)
    {
        if (_disposed) return;
        try {  await InvokeAsync(StateHasChanged); } catch { /* component disposed */ }
    }

    // ── Navigation Event Handlers ───────────────────────────────────────

    private void OnManeuverChanged(NavigationManeuverInfo info)
    {
        if (_disposed) return;
        _navManeuver = info;
        _navProgress = info.Total > 0
            ? (double)(info.Index + 1) / info.Total * 100
            : 0;
        InvokeAsync(StateHasChanged);
    }

    private void OnStatusUpdated(NavigationStatus status)
    {
        if (_disposed) return;
        _navStatus = status;
        // Use DisplayManeuverIndex (look-ahead) for progress if available, otherwise fall back to physical index
        int progressIndex = status.DisplayManeuverIndex > 0 ? status.DisplayManeuverIndex : status.CurrentManeuverIndex;
        _navProgress = status.TotalManeuvers > 0
            ? Math.Clamp((double)(progressIndex + 1) / status.TotalManeuvers * 100, 0, 100)
            : 0;
        InvokeAsync(StateHasChanged);
    }

    private void OnNavigationCompleted()
    {
        if (_disposed) return;
        Snackbar.Add(L["DestinationReached"], Severity.Success);

        // GPS + Breadcloth nicht stoppen – User kann manuell Stoppen
        // Aber NavService hat bereits StopNavigation() aufgerufen
        // → UI schaltet via NavigationStateChanged zurück in Plan-Modus
        InvokeAsync(StateHasChanged);
    }

    private void OnNavigationStateChanged(bool isNavigating)
    {
        if (_disposed) return;
        _isNavigating = isNavigating;
        if (!isNavigating)
        {
            _navManeuver = null;
            _navStatus = null;
            _navProgress = 0;
        }
        InvokeAsync(StateHasChanged);
    }
    
    // ── Map Marker Helpers ──────────────────────────────────────────────

    private async Task UpdatePositionMarkerAsync(GpsReading reading)
    {
        if (_disposed) return;
        OpenStreetMap? map = _map;
        if (map == null) return;

        // Prevent reentrancy (ObservableCollection mutation during CollectionChanged)
        if (Interlocked.CompareExchange(ref _markerUpdating, 1, 0) != 0) return;

        try
        {
            double lon = reading.Position.Longitude;
            double lat = reading.Position.Latitude;
            OpenLayers.Blazor.Coordinate coord = new(lon, lat);

            if (!_gpsMarkerInitialized)
            {
                // Create the position marker (blue dot)
                Marker marker = new()
                {
                    Coordinate = coord,
                    Type = MarkerType.MarkerPin,
                    PinColor = PinColor.Blue
                };
                map.MarkersList.Add(marker);

                // Create a heading indicator (arrow marker)
                double headingRad = reading.Heading * Math.PI / 180;
                Marker headingMarker = new()
                {
                    Coordinate = coord,
                    Type = MarkerType.MarkerAwesome,
                    PinColor = PinColor.Green,
                    Rotation = headingRad
                };
                map.MarkersList.Add(headingMarker);

                _lastMarkerLon = lon;
                _lastMarkerLat = lat;
                _hasLastMarkerCoord = true;
                _gpsMarkerInitialized = true;
            }
            else
            {
                // 1. Time-based throttle: every 2s max to avoid IPC flood
                DateTime now = DateTime.UtcNow;
                if ((now - _lastMarkerUpdate).TotalSeconds < 2) return;

                // 2. Distance-based throttle: only update if moved > 1 meter
                if (_hasLastMarkerCoord)
                {
                    double dist = GeoMath.DistanceMeters(
                        _lastMarkerLat, _lastMarkerLon,
                        lat, lon);
                    if (dist < 1.0) return;
                }

                _lastMarkerUpdate = now;
                _lastMarkerLon = lon;
                _lastMarkerLat = lat;

                // Update markers in-place via MarkersList indexer (avoids Clear+Add JS flood)
                if (map.MarkersList.Count > 0)
                {
                    map.MarkersList[0] = new Marker
                    {
                        Coordinate = coord,
                        Type = MarkerType.MarkerPin,
                        PinColor = PinColor.Blue
                    };
                }

                if (map.MarkersList.Count >= 2)
                {
                    double headingRad = reading.Heading * Math.PI / 180;
                    MarkerType oldType = ((Marker)map.MarkersList[1]).Type;
                    map.MarkersList[1] = new Marker
                    {
                        Coordinate = coord,
                        Type = oldType,
                        PinColor = PinColor.Green,
                        Rotation = headingRad,
                        Text = "➤"
                    };
                }
            }

            // Update breadcrumb
            await UpdateBreadcrumbAsync();
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

        // Also remove breadcrumb layer if present
        Layer? breadcrumbLayer = map.LayersList?
            .FirstOrDefault(l => l.Id == BreadcrumbLayerId);
        if (breadcrumbLayer != null)
            await map.RemoveLayer(breadcrumbLayer);
    }

    private async Task UpdateBreadcrumbAsync()
    {
        if (_disposed || Gps.Breadcrumb.Count < 2) return;
        OpenStreetMap? map = _map;
        if (map == null) return;

        // Throttle: nur alle 10 Sekunden updaten, nicht bei jedem GPS-Tick
        DateTime now = DateTime.UtcNow;
        if ((now - _lastBreadcrumbUpdate).TotalSeconds < 10)
            return;
        _lastBreadcrumbUpdate = now;

        if (Interlocked.CompareExchange(ref _breadcrumbUpdating, 1, 0) != 0) return;

        try
        {
            Layer? existing = map.LayersList?
                .FirstOrDefault(l => l.Id == BreadcrumbLayerId);

            if (existing == null)
            {
                // Layer einmalig anlegen
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
            // else: Layer existiert bereits – nicht updaten!
            // Kein RemoveLayer/AddLayer -> Karte bleibt interaktiv
        }
        finally
        {
            Interlocked.Exchange(ref _breadcrumbUpdating, 0);
        }
    }

    private async Task SetMapLoaded()
    {
        _mapLoaded = true;
        OpenStreetMap? map = _map;

        await Gps.RequestPermissionAsync();

        // Default-Start auf "Aktuelle Position" wenn keine Route geladen
        if (string.IsNullOrWhiteSpace(RouteId) && _startLocation == null && Gps.LastReading != null)
        {
            _startLocation = new GeoResult(
                L["CurrentPos"],
                Gps.LastReading.Position.Latitude,
                Gps.LastReading.Position.Longitude,
                "current");
            StateHasChanged();
        }
    }

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

    protected override async Task OnParametersSetAsync()
    {
        if (!string.IsNullOrWhiteSpace(RouteId))
        {
            try
            {
                SavedRoute? savedRoute = await RouteStorage.GetRouteByIdAsync(RouteId);

                if (savedRoute != null && !string.IsNullOrEmpty(savedRoute.Polyline6) && savedRoute.Waypoints is { Count: >= 2 })
                {
                    await WaitForMapLoadedAsync();
                    
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
                    await _map?.SetCenter(new OpenLayers.Blazor.Coordinate(startCoord.Longitude, startCoord.Latitude));

                    Snackbar.Add(string.Format(L["RouteLoaded"], savedRoute.Name), Severity.Info);

                    // Automatisch Route via Valhalla berechnen (für Navigation starten + Turn-by-Turn)
                    await CalculateRoute();
                }
                else
                {
                    Snackbar.Add(L["RouteLoadFailed"], Severity.Error);
                }
            }
            catch (Exception e)
            {
                Snackbar.Add(L["RouteLoadFailed"], Severity.Error);
                Console.WriteLine(e);
            }
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
        List<GeoResult> pins = GetPinnedStartLocations();

        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return pins;

        List<GeoResult> results = await GeocodingService.SuggestAsync(query, ct: ct);

        if (results.Count == 0)
            return pins;

        // Pins + Trenner + Suchergebnisse
        return pins
            .Append(new GeoResult("———", 0, 0, "separator"))
            .Concat(results);
    }

    private static Location CreateLocation(GeoResult? location, string type = "break")
    {
        return new Location { Lat = location!.Latitude, Lon = location.Longitude, Type = type };
    }

    /// <summary>
    /// Wartet auf den ersten GPS-Fix, indem es den ReadingReceived-Event in
    /// einen IAsyncEnumerable verpackt.
    /// </summary>
    private async IAsyncEnumerable<GpsReading> WaitForGpsFixAsync([EnumeratorCancellation] CancellationToken ct)
    {
        GpsReading? result = null;
        TaskCompletionSource<GpsReading> tcs = new();

        void Handler(GpsReading reading)
        {
            if (result == null)
            {
                result = reading;
                tcs.TrySetResult(reading);
            }
        }

        Gps.ReadingReceived += Handler;
        try
        {
            await using (ct.Register(() => tcs.TrySetCanceled()))
            {
                GpsReading reading = await tcs.Task;
                yield return reading;
            }
        }
        finally
        {
            Gps.ReadingReceived -= Handler;
        }
    }

    private const int MaxViaLocations = 48;

    // ── Route Calculation ──

    private async Task CalculateRoute()
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
        double startLat, startLon;
        if (_startLocation?.Type == "current")
        {
            if (Gps.LastReading != null)
            {
                startLat = Gps.LastReading.Position.Latitude;
                startLon = Gps.LastReading.Position.Longitude;
            }
            else
            {
                // GPS starten und auf ersten Fix warten
                if (!Gps.IsTracking)
                {
                    try { await Gps.StartTrackingAsync(backgroundMode: true); }
                    catch (Exception ex)
                    {
                        Snackbar.Add(string.Format(L["GPSStartFailed"], ex.Message), Severity.Error);
                        _isLoading = false;
                        return;
                    }
                }

                // Warten auf ersten GPS-Fix (max 10s)
                GpsReading? firstFix = null;
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
                try
                {
                    await foreach (GpsReading reading in WaitForGpsFixAsync(cts.Token))
                    {
                        firstFix = reading;
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    Snackbar.Add(L["GPSNoFixTimeout"], Severity.Error);
                    _isLoading = false;
                    return;
                }

                if (firstFix == null)
                {
                    Snackbar.Add(L["GPSNoSignal"], Severity.Error);
                    _isLoading = false;
                    return;
                }

                startLat = firstFix.Position.Latitude;
                startLon = firstFix.Position.Longitude;
            }
        }
        else if (_startLocation != null)
        {
            startLat = _startLocation.Latitude;
            startLon = _startLocation.Longitude;
        }
        else
        {
            Snackbar.Add(L["NoStartSelected"], Severity.Error);
            _isLoading = false;
            return;
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

        _isLoading = false;
        StateHasChanged();
    }

    /// <summary>
    /// Reduces <see cref="_waypoints"/> to at most <paramref name="maxCount"/>
    /// using greedy farthest-point sampling (preserves first &amp; last).
    /// </summary>
    private void ReduceWaypointsToMax(int maxCount)
    {
        if (_waypoints.Count <= maxCount) return;

        List<WaypointViewModel> kept =
        [
            _waypoints[0]
        ];

        // Build list of (index, location) for simplification
        List<(int Index, GeoResult Location)> points = [];
        for (int i = 1; i < _waypoints.Count; i++)
        {
            if (_waypoints[i].Location is { } loc)
            {
                points.Add((i, loc));
            }
        }

        // Simple farthest-point-sampling: iteratively pick the point farthest
        // from the nearest already-selected point until we have maxCount.
        int pointsToKeep = maxCount - 1; // -1 for first already kept
        if (points.Count <= pointsToKeep)
        {
            kept.AddRange(
                points.Select(p => _waypoints[p.Index]));
            kept.Add(_waypoints[^1]);
            _waypoints = kept;
            return;
        }

        // Greedy farthest-point sampling
        List<(int Index, GeoResult Location)> selected = [points[0], points[^1]];
        HashSet<int> selectedIndices = [points[0].Index, points[^1].Index];

        while (selected.Count < pointsToKeep)
        {
            // Find farthest point from any selected point
            double maxMinDist = -1;
            int bestIndex = -1;

            foreach ((int idx, GeoResult loc) in points)
            {
                if (selectedIndices.Contains(idx)) continue;

                double minDist = double.MaxValue;
                foreach ((int _, GeoResult selLoc) in selected)
                {
                    double d = GeoMath.DistanceKm(
                        loc.Latitude, loc.Longitude,
                        selLoc.Latitude, selLoc.Longitude);
                    if (d < minDist) minDist = d;
                }

                if (minDist > maxMinDist)
                {
                    maxMinDist = minDist;
                    bestIndex = idx;
                }
            }

            if (bestIndex < 0) break;

            selectedIndices.Add(bestIndex);
            selected.Add(points.First(p => p.Index == bestIndex));
        }

        // Rebuild _waypoints with only the selected indices
        kept.Clear();
        kept.Add(_waypoints[0]);
        foreach ((int idx, _) in selected.OrderBy(s => s.Index))
        {
            kept.Add(_waypoints[idx]);
        }
        if (kept[^1] != _waypoints[^1])
        {
            kept.Add(_waypoints[^1]);
        }

        _waypoints = kept;
    }

    private async Task SaveCurrentRoute()
    {
        if (_currentRouteResponse?.Trip == null || _currentRouteResponse?.Trip?.Locations?.Count == 0) return;

        // Dialog: Name für die Route abfragen
        // Bestehende Route aktualisieren → kein Dialog nötig
        if (!string.IsNullOrWhiteSpace(RouteId))
        {
            _isSaving = true;
            try
            {
                List<Leg> legs = _currentRouteResponse!.Trip!.Legs ?? [];
                (string combinedShape, _, double totalDistanceKm, double totalTimeMinutes) = PrepareNavigationData(legs);

                SavedRoute? existing = await RouteStorage.GetRouteByIdAsync(RouteId);
                if (existing != null)
                {
                    existing.Polyline6 = combinedShape;
                    existing.DistanceKm = Math.Round(totalDistanceKm, 2);
                    existing.TimeMinutes = Math.Round(totalTimeMinutes, 2);
                    existing.Waypoints ??= [];
                    existing.Waypoints.Clear();
                    foreach (Location wp in _currentRouteResponse.Trip!.Locations!)
                    {
                        List<GeoResult> reverseGeo = await GeocodingService.GetReverseGeocodingAsync(wp.Lon, wp.Lat);
                        existing.Waypoints.Add(new Models.Valhalla.Coordinate(wp.Lat, wp.Lon, reverseGeo.FirstOrDefault()?.Label ?? "Waypoint"));
                    }
                    await RouteStorage.SaveRouteAsync(existing);
                    Snackbar.Add(L["RouteSaved"], Severity.Success);
                }
                else
                {
                    Snackbar.Add(L["RouteLoadFailed"], Severity.Error);
                }
            }
            finally { _isSaving = false; }
            return;
        }

        // Neue Route → Dialog nach Namen fragen
        DialogParameters<RenameDialog> parameters = new()
        {
            { nameof(RenameDialog.ContentText), (string)L["SaveDialogPrompt"] },
            { nameof(RenameDialog.ButtonText), (string)L["Save"] },
            { "Color", MudBlazor.Color.Primary }
        };
        IDialogReference dialog = await DialogService.ShowAsync<RenameDialog>(
            L["SaveDialogTitle"],parameters);
        DialogResult? result = await dialog.Result;

        if (result.Canceled || result.Data is not string routeName || string.IsNullOrWhiteSpace(routeName))
            return;

        _isSaving = true;
        try
        {
            List<Leg> legs = _currentRouteResponse!.Trip!.Legs ?? [];
            (string combinedShape, _, double totalDistanceKm, double totalTimeMinutes) = PrepareNavigationData(legs);

            SavedRoute savedRoute = new()
            {
                Name = routeName,
                Polyline6 = combinedShape,
                DistanceKm = Math.Round(totalDistanceKm, 2),
                TimeMinutes = Math.Round(totalTimeMinutes, 2),
                Waypoints = []
            };

            foreach (Location wp in _currentRouteResponse.Trip!.Locations!)
            {
                List<GeoResult> reverseGeo = await GeocodingService.GetReverseGeocodingAsync(wp.Lon, wp.Lat);
                savedRoute.Waypoints.Add(new Models.Valhalla.Coordinate(wp.Lat, wp.Lon, reverseGeo.FirstOrDefault()?.Label ?? "Waypoint"));
            }

            await RouteStorage.SaveRouteAsync(savedRoute);
            Snackbar.Add(L["RouteSaved"], Severity.Success);
        }
        finally { _isSaving = false; }
    }

    // ── Start Navigation ────────────────────────────────────────────────

    /// <summary>
    /// Merges all leg shapes into a single polyline6 string, collecting all maneuvers
    /// with adjusted shape indices.
    /// </summary>
    private static (string MergedShape, List<Maneuver> Maneuvers, double TotalKm, double TotalMin)
        PrepareNavigationData(IReadOnlyList<Leg> legs)
    {
        List<Coordinate> allCoords = [];
        List<Maneuver> allManeuvers = [];
        int shapeOffset = 0;

        foreach (Leg leg in legs)
        {
            List<Coordinate>? legCoords = null;
            if (!string.IsNullOrEmpty(leg.Shape))
            {
                legCoords = PolylineEncoder.DecodePolyline6(leg.Shape);
                if (legCoords.Count > 0)
                {
                    if (allCoords.Count > 0)
                        allCoords.AddRange(legCoords.Skip(1));
                    else
                        allCoords.AddRange(legCoords);
                }
            }

            if (leg.Maneuvers != null)
            {
                foreach (Maneuver m in leg.Maneuvers)
                {
                    Maneuver clone = new()
                    {
                        Type = m.Type,
                        Instruction = m.Instruction,
                        BeginShapeIndex = m.BeginShapeIndex + shapeOffset,
                        EndShapeIndex = m.EndShapeIndex + shapeOffset,
                        Length = m.Length,
                        Time = m.Time,
                        TravelMode = m.TravelMode,
                        TravelType = m.TravelType,
                        TurnDegree = m.TurnDegree,
                        RoundaboutExitCount = m.RoundaboutExitCount,
                        RoundaboutExit = m.RoundaboutExit,
                        StreetNames = m.StreetNames,
                        VerbalSuccinctInstruction = m.VerbalSuccinctInstruction,
                        Sign = m.Sign
                    };
                    allManeuvers.Add(clone);
                }
            }

            // Reuse cached decode for shape offset calculation
            if (legCoords is { Count: > 0 })
                shapeOffset += legCoords.Count - 1;
        }

        string mergedShape = PolylineEncoder.EncodePolyline6(allCoords);
        double totalKm = legs.Sum(l => l.Summary?.Length ?? 0);
        double totalMin = legs.Sum(l => l.Summary?.Time ?? 0) / 60.0;

        return (mergedShape, allManeuvers, totalKm, totalMin);
    }

    private async Task StartNavigation()
    {
        if (_currentRouteResponse?.Trip?.Legs == null || _currentRouteResponse.Trip.Legs.Count == 0)
        {
            Snackbar.Add(L["NoRoute"], Severity.Error);
            return;
        }

        try
        {
            (string mergedShape, List<Maneuver> allManeuvers, double totalKm, double totalMin) =
                PrepareNavigationData(_currentRouteResponse.Trip.Legs);
            await NavService.StartNavigation(mergedShape, allManeuvers, totalKm, totalMin);

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
        // NavService.StopNavigation() handles GPS stop internally
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

    private async void OnOffRouteDetected(double lat, double lon, double distanceMeters)
    {
        if (_disposed || _destinationLocation == null || _isLoading) return;

        // Cooldown: max one reroute every 30 seconds to prevent thrashing
        if ((DateTime.UtcNow - _lastRerouteTime).TotalSeconds < 30) return;
        _lastRerouteTime = DateTime.UtcNow;

        Snackbar.Add(string.Format(L["OffRouteDetected"], distanceMeters), Severity.Warning);

        await RerouteAsync(lat, lon);
    }

    private async Task RerouteAsync(double currentLat, double currentLon)
    {
        if (_isLoading) return;
        _isLoading = true;

        try
        {
            // Valhalla-Request: aktuelle Position → Ziel
            List<Location> locs =
            [
                new() { Lat = currentLat, Lon = currentLon, Type = "break" },
                CreateLocation(_destinationLocation)
            ];

            RouteRequest request = new()
            {
                Locations = locs,
                Costing = "motorcycle",
                DirectionsOptions = new DirectionsOptions { Units = "km" }
            };

            Result result = await ValhallaClient.GetRouteAsync(request);
            if (!result.IsSuccess)
            {
                Snackbar.Add(string.Format(L["RerouteFailed"], result.ErrorMessage), Severity.Error);
                return;
            }

            RouteResponse response = result.Response!;
            if (response.Trip?.Legs == null || response.Trip.Legs.Count == 0)
            {
                Snackbar.Add(L["RerouteNoRoute"], Severity.Error);
                return;
            }

            // Neue Route auf Karte anzeigen
            _currentRouteResponse = response;
            await ShowRouteOnMap(response);

            // NavService mit neuer Route updaten
            (string mergedShape, List<Maneuver> allManeuvers, double totalKm, double totalMin) =
                PrepareNavigationData(response.Trip.Legs);
            NavService.Reroute(mergedShape, allManeuvers, totalKm, totalMin);

            Snackbar.Add(L["RouteRecalculated"], Severity.Success);
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
    }

    private async Task ShowRouteOnMap(RouteResponse response)
    {
        if (response.Trip?.Legs == null) return;
        OpenStreetMap? map = _map;
        if (map == null) return;

        (string combinedShape, _, _, _) = PrepareNavigationData(response.Trip.Legs);

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
            if (map.MarkersList.Count > 2)
            {
                List<Shape> gpsMarkers = [.. map.MarkersList.Take(2)];
                map.MarkersList.Clear();
                StateHasChanged();
                foreach (Shape m in gpsMarkers) map.MarkersList.Add(m);
            }

            List<Location> locations = response.Trip.Locations;
            for (int i = 0; i < locations.Count; i++)
            {
                Location loc = locations[i];
                OpenLayers.Blazor.Coordinate coord = new(loc.Lon, loc.Lat);
                
                // Color logic: Red for destination, Green for start/waypoints
                PinColor color = PinColor.Green;
                if (i == locations.Count - 1)
                {
                    color = PinColor.Red;
                }

                map.MarkersList.Add(new Marker(MarkerType.MarkerPin, coord, "", color));
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
            OpenLayers.Blazor.Coordinate coord = new(
                Gps.LastReading.Position.Longitude,
                Gps.LastReading.Position.Latitude);
            await map.SetCenter(coord);
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

        NavService.ManeuverChanged -= OnManeuverChanged;
        NavService.StatusUpdated -= OnStatusUpdated;
        NavService.NavigationCompleted -= OnNavigationCompleted;
        NavService.NavigationStateChanged -= OnNavigationStateChanged;
        NavService.OffRouteDetected -= OnOffRouteDetected;

        // Map-Referenz löschen – JS-Objekt existiert dann ggf. nicht mehr
        _map = null;

        if (Gps.IsTracking)
        {
            await Gps.StopTrackingAsync();
        }
    }
}
