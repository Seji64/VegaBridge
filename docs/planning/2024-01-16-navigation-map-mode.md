# Navigation Map Mode — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add navigation HUD overlay to `Map.razor` when `NavService.IsNavigating` is true, replacing the separate `/navigation` page.

**Architecture:** `Map.razor` conditionally renders either the route-planning panel (current) or a navigation HUD overlay. Same `OpenStreetMap`, GPS markers, breadcrumb. No page navigation. `Navigation.razor` stays as reference.

**Tech Stack:** .NET MAUI Blazor Hybrid, MudBlazor, OpenLayers.Blazor

## Global Constraints

- No page navigation on nav start — stay on `/map`
- Map must remain interactive (zoom/pan) at all times — HUD is pure overlay
- Subscribe to NavService events in OnInitialized, unsubscribe in Dispose
- Use `@if (NavService.IsNavigating)` for mode switching; map always unconditionally rendered

---

### Task 1: NavService subscriptions + state fields (Map.razor.cs)

**Files:**
- Modify: `src/VegaBridgeApp/Components/Pages/Map.razor.cs`

**Interfaces:**
- Consumes: `NavService.ManeuverChanged`, `NavService.StatusUpdated`, `NavService.NavigationCompleted`, `NavService.NavigationStateChanged`, `NavService.BleCommandSimulated`
- Consumes: `NavigationManeuverInfo` (fields: Index, Total, Instruction, BLEIcon)
- Consumes: `NavigationStatus` (fields: DistanceToNextTurnM, RemainingDistanceKm, RemainingTimeMin, CurrentManeuverIndex, TotalManeuvers)
- Produces: `_navManeuver`, `_navStatus`, `_navProgress`, `_bleLog`, `_showBleLog` fields + handlers

- [ ] **Step 1: Add nav state fields** after existing field block (around line 30)

```csharp
    // ── Navigation state ──
    private NavigationManeuverInfo? _navManeuver;
    private NavigationStatus? _navStatus;
    private double _navProgress;
    private readonly List<string> _bleLog = [];
    private bool _showBleLog;
```

- [ ] **Step 2: Subscribe to NavService events** in `OnInitialized()` after existing Gps subscriptions

```csharp
        NavService.ManeuverChanged += OnManeuverChanged;
        NavService.StatusUpdated += OnStatusUpdated;
        NavService.NavigationCompleted += OnNavigationCompleted;
        NavService.NavigationStateChanged += OnNavigationStateChanged;
        NavService.BleCommandSimulated += OnBleCommand;
```

- [ ] **Step 3: Add handler methods** before `// ── Map Marker Helpers ──` comment

```csharp
    // ── Navigation Event Handlers ───────────────────────────────────────

    private void OnManeuverChanged(NavigationManeuverInfo info)
    {
        _navManeuver = info;
        _navProgress = info.Total > 0
            ? (double)(info.Index + 1) / info.Total * 100
            : 0;
        InvokeAsync(StateHasChanged);
    }

    private void OnStatusUpdated(NavigationStatus status)
    {
        _navStatus = status;
        _navProgress = status.TotalManeuvers > 0
            ? Math.Clamp((double)(status.CurrentManeuverIndex + 1) / status.TotalManeuvers * 100, 0, 100)
            : 0;
        InvokeAsync(StateHasChanged);
    }

    private void OnNavigationCompleted()
    {
        Snackbar.Add("Ziel erreicht! 🏁", Severity.Success);
        InvokeAsync(StateHasChanged);
    }

    private void OnNavigationStateChanged(bool isNavigating)
    {
        if (!isNavigating)
        {
            _navManeuver = null;
            _navStatus = null;
            _navProgress = 0;
            _showBleLog = false;
            InvokeAsync(StateHasChanged);
        }
    }

    private void OnBleCommand(string command, byte[] frame)
    {
        string hex = BitConverter.ToString(frame).Replace("-", " ");
        string text = System.Text.Encoding.UTF8.GetString(frame).Replace("\r", "⏎").Replace("\x1E", "⏝");
        _bleLog.Add($"{command}: {text} [{hex}]");
        if (_bleLog.Count > 200)
            _bleLog.RemoveRange(0, 100);
        InvokeAsync(StateHasChanged);
    }

    private void ToggleBleLog()
    {
        _showBleLog = !_showBleLog;
    }
```

- [ ] **Step 4: Build check**

```bash
cd src/VegaBridgeApp && dotnet build -f net10.0-maccatalyst 2>&1 | tail -20
```

---

### Task 2: Update StartNavigation + add ExitNavigation (Map.razor.cs)

**Files:**
- Modify: `src/VegaBridgeApp/Components/Pages/Map.razor.cs`

**Interfaces:**
- Consumes: `NavService.StartNavigation(...)`, `NavService.StopNavigation()`
- Consumes: `Gps.StopTrackingAsync()`, `Gps.ClearBreadcrumb()`
- Produces: `ExitNavigation()` method

- [ ] **Step 1: Remove page navigation** from `StartNavigation()` method — delete the `NavManager.NavigateTo("/navigation");` line (around line 615)

Find and remove:
```csharp
            NavManager.NavigateTo("/navigation");
```

- [ ] **Step 2: Add ExitNavigation method** after `StartNavigation()` method

```csharp
    private async Task ExitNavigation()
    {
        NavService.StopNavigation();
        if (Gps.IsTracking)
        {
            Gps.ClearBreadcrumb();
            await Gps.StopTrackingAsync();
            await ClearGpsMarkersAsync();
        }
        Snackbar.Add("Navigation beendet", Severity.Info);
    }
```

- [ ] **Step 3: Build check**

```bash
cd src/VegaBridgeApp && dotnet build -f net10.0-maccatalyst 2>&1 | tail -20
```

---

### Task 3: Plan/Navi mode UI (Map.razor)

**Files:**
- Modify: `src/VegaBridgeApp/Components/Pages/Map.razor`

**Interfaces:**
- Reads: `NavService.IsNavigating`, `Gps.CurrentSpeedKmh`, `Gps.IsTracking`, `Gps.CurrentAccuracy`
- Reads: `_navManeuver`, `_navStatus`, `_navProgress`, `_showBleLog`, `_bleLog`

- [ ] **Step 1: Wrap search panel** in `@if (!NavService.IsNavigating)`

The search panel starts at the MudPaper with `Elevation="4"` and ends before the `<!-- Karte -->` div. Wrap the entire block:

```razor
    @if (!NavService.IsNavigating)
    {
        <!-- Top Navigation Bar (AppBar Style) -->
        <MudPaper Elevation="4"
                  Style="position: fixed; top: 0; left: 0; right: 0; z-index: 1300; width: 100%;"
                  Class="pa-4 mt-4">
            ...
        </MudPaper>
    }
```

- [ ] **Step 2: Add navigation HUD** after the closing `}` of the plan-mode block, before the `<!-- Karte -->` div

```razor
    @if (NavService.IsNavigating)
    {
        <!-- Top Bar -->
        <MudPaper Elevation="4"
                  Style="position: fixed; top: 0; left: 0; right: 0; z-index: 1300; width: 100%;"
                  Class="px-4 py-2 mt-4">
            <MudStack Row="true" Justify="Justify.FlexStart" AlignItems="AlignItems.Center">
                @if (_navManeuver != null)
                {
                    <MudChip T="string" Size="Size.Small" Color="Color.Primary" Class="mr-2">
                        @($"Manöver {_navManeuver.Index + 1}/{_navManeuver.Total}")
                    </MudChip>
                }
                <MudChip T="string" Size="Size.Small" Color="Color.Info">
                    @($"±{Gps.CurrentAccuracy:F0} m")
                </MudChip>
                <MudIconButton Icon="@Icons.Material.Filled.Terminal"
                               Size="Size.Small"
                               Color="@(_showBleLog ? Color.Primary : Color.Default)"
                               OnClick="@ToggleBleLog"
                               Class="ml-auto" />
            </MudStack>
        </MudPaper>

        <!-- Speed - top-right overlay -->
        <div Style="position: fixed; top: 100px; right: 20px; z-index: 1299; text-align: right; text-shadow: 0 2px 8px rgba(0,0,0,0.7);">
            <MudText Style="font-size: 5rem; font-weight: 700; line-height: 1; letter-spacing: -2px; color: white;">
                @Gps.CurrentSpeedKmh.ToString("F0")
            </MudText>
            <MudText Typo="Typo.h6" Style="color: rgba(255,255,255,0.8);">km/h</MudText>
        </div>

        <!-- Next Turn - bottom card -->
        @if (_navManeuver != null)
        {
            <MudPaper Style="position: fixed; bottom: 120px; left: 16px; right: 96px; z-index: 1299; border-radius: 12px; background: rgba(30,30,30,0.85); backdrop-filter: blur(8px);"
                      Class="pa-3">
                <MudStack Spacing="1">
                    <MudText Style="font-size: 2rem; text-align: center;">
                        @GetTurnEmoji(_navManeuver.BLEIcon)
                    </MudText>
                    <MudText Typo="Typo.subtitle1" Align="Align.Center" Style="color: white;">
                        @_navManeuver.Instruction
                    </MudText>
                    @if (_navStatus != null)
                    {
                        <MudText Typo="Typo.h5" Align="Align.Center" Color="Color.Info">
                            @(_navStatus.DistanceToNextTurnM.ToString("F0")) m
                        </MudText>
                    }
                </MudStack>
            </MudPaper>
        }

        <!-- Progress - bottom bar -->
        @if (_navProgress > 0)
        {
            <div Style="position: fixed; bottom: 60px; left: 0; right: 0; z-index: 1299; padding: 0 16px;">
                <MudStack Spacing="1">
                    <MudProgressLinear Value="@((int)_navProgress)" Color="Color.Primary" Size="Size.Small" />
                    <MudStack Row="true" Justify="Justify.SpaceBetween">
                        <MudText Typo="Typo.caption" Style="color: rgba(255,255,255,0.7); text-shadow: 0 1px 4px rgba(0,0,0,0.5);">
                            @(_navStatus?.RemainingDistanceKm.ToString("F1")) km
                        </MudText>
                        <MudText Typo="Typo.caption" Style="color: rgba(255,255,255,0.7); text-shadow: 0 1px 4px rgba(0,0,0,0.5);">
                            @FormatTime(_navStatus?.RemainingTimeMin)
                        </MudText>
                    </MudStack>
                </MudStack>
            </div>
        }

        <!-- BLE Log overlay -->
        @if (_showBleLog)
        {
            <MudPaper Style="position: fixed; top: 80px; right: 16px; z-index: 1400; width: 360px; max-height: 50vh; background: rgba(10,10,10,0.92); border: 1px solid #0f0; border-radius: 8px; overflow-y: auto;"
                      Class="pa-3">
                <MudStack Row="true" Justify="Justify.SpaceBetween" AlignItems="AlignItems.Center" Class="mb-2">
                    <MudText Typo="Typo.overline" Style="color: #0f0;">BLE-Kommandos</MudText>
                    <MudIconButton Icon="@Icons.Material.Filled.ContentCopy"
                                   Size="Size.Small"
                                   Color="Color.Primary"
                                   OnClick="@CopyBleLog"
                                   Title="Log kopieren" />
                </MudStack>
                <textarea readonly class="ble-log-textarea" rows="8">@string.Join(Environment.NewLine, _bleLog.TakeLast(50))</textarea>
            </MudPaper>
        }

        <!-- Stop Navigation button (bottom-right, above GPS FAB) -->
        <MudFab Color="Color.Error"
                Icon="@Icons.Material.Filled.Stop"
                Size="Size.Small"
                Style="position: absolute; bottom: 160px; right: 16px; z-index: 1298;"
                OnClick="@ExitNavigation" />
    }
```

- [ ] **Step 3: Add helper methods** `GetTurnEmoji` and `FormatTime` (copy from `Navigation.razor.cs`) to `Map.razor.cs`

Add after `ExitNavigation()` method:
```csharp
    private static string GetTurnEmoji(string bleIcon)
    {
        return bleIcon switch
        {
            "turn-left" => "⬅️",
            "turn-right" => "➡️",
            "turn-slight-left" => "↖️",
            "turn-slight-right" => "↗️",
            "uturn-left" or "uturn-right" => "🔃",
            "straight" => "⬆️",
            "Finish" => "🏁",
            "roundabout-left-1" or "roundabout-left-2" => "🔄",
            "roundabout-right-1" or "roundabout-right-2" => "🔄",
            _ => "⬆️"
        };
    }

    private static string FormatTime(double? minutes)
    {
        if (minutes == null || minutes < 0) return "–";
        if (minutes.Value < 1) return "< 1 Min";
        if (minutes.Value < 60)
            return $"{(int)minutes.Value} Min";
        int hours = (int)(minutes.Value / 60);
        int mins = (int)(minutes.Value % 60);
        return $"{hours}h {mins} Min";
    }
```

- [ ] **Step 4: Add CopyBleLog method** (from Navigation.razor.cs) to `Map.razor.cs`

```csharp
    private async Task CopyBleLog()
    {
        string logText = string.Join(Environment.NewLine, _bleLog.TakeLast(50));
        if (string.IsNullOrEmpty(logText)) return;
        try
        {
            await Clipboard.Default.SetTextAsync(logText);
            Snackbar.Add("BLE-Log kopiert! 📋", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Kopieren fehlgeschlagen: {ex.Message}", Severity.Error);
        }
    }
```

- [ ] **Step 5: Add BLE log textarea style** to existing `<style>` block at bottom of Map.razor

```css
    .ble-log-textarea {
        width: 100%;
        background: #0a0a0a;
        color: #0f0;
        border: none;
        font-family: 'Courier New', monospace;
        font-size: 0.7rem;
        line-height: 1.4;
        resize: vertical;
        outline: none;
        padding: 4px;
    }
```

- [ ] **Step 6: Build check**

```bash
cd src/VegaBridgeApp && dotnet build -f net10.0-maccatalyst 2>&1 | tail -20
```

---

### Task 4: Update Dispose (Map.razor.cs)

**Files:**
- Modify: `src/VegaBridgeApp/Components/Pages/Map.razor.cs`

- [ ] **Step 1: Unsubscribe NavService events** in `DisposeAsync()` after existing Gps unsubscriptions

```csharp
        NavService.ManeuverChanged -= OnManeuverChanged;
        NavService.StatusUpdated -= OnStatusUpdated;
        NavService.NavigationCompleted -= OnNavigationCompleted;
        NavService.NavigationStateChanged -= OnNavigationStateChanged;
        NavService.BleCommandSimulated -= OnBleCommand;
```

- [ ] **Step 2: Build check**

```bash
cd src/VegaBridgeApp && dotnet build -f net10.0-maccatalyst 2>&1 | tail -20
```

---

### Task 5: Verify compilation

**Files:**
- N/A — build verification only

- [ ] **Step 1: Full build**

```bash
cd src/VegaBridgeApp && dotnet build -f net10.0-maccatalyst 2>&1 | tail -30
```

Expected: Build succeeded with no errors. (Warnings from unused `NavigationManager NavManager` injection in Map.razor are acceptable — it's still used by the route redirect logic.)
