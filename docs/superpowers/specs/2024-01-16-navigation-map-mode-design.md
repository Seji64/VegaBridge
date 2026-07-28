# Navigation Map Mode — Design Spec

## Problem

The current `Navigation.razor` page shows turn-by-turn info (speed, next turn, progress, BLE log) but **no map**. When the user starts navigation from `Map.razor`, the app navigates to `/navigation` and the map disappears. To see the map again, the user must stop navigation and return to `/map`.

Goal: Show a map during navigation so the rider can see the route, their GPS position, and zoom/pan freely — without having to stop navigation.

## Solution

**Option B** (chosen): `Map.razor` gains a **Navi-Modus** (`NavService.IsNavigating == true`). The same page switches between:

- **Plan-Modus** — route search, waypoints, calculate/save route (current behavior)
- **Navi-Modus** — map full-screen with HUD overlay (speed, turn, progress, stop button)

The change is purely UI: the `OpenStreetMap`, GPS markers, breadcrumb, and route polyline remain identical between modes.

## Layout

### Plan-Modus (existing, unchanged)

```
┌──────────────────────────────┐
│ Fixed top panel              │
│  GPS chip (if tracking)      │
│  Start / Waypoints / Ziel    │
│  [Route berechnen]           │
│  [Navigation starten]        │
│  [Route speichern]           │
├──────────────────────────────┤
│ OpenStreetMap                │
│                   [📍 FAB]   │
└──────────────────────────────┘
```

### Navi-Modus (new)

```
┌──────────────────────────────┐
│ Minimal top bar              │
│  [1/8] [±3m] [🖥 BLE-Log]   │
├──────────────────────────────┤
│                              │
│         ┌──────┐             │
│         │ 68   │  ← Speed    │
│         │ km/h │             │
│         └──────┘             │
│                              │
│     OpenStreetMap            │
│     (full height/width)      │
│     → Route-Linie            │
│     → GPS-Marker + Heading  │
│     → Breadcrumb-Trail      │
│                              │
│   ┌────────────────────┐     │
│   │ ⬅️ Links abbiegen   │     │  ← Next Turn
│   │ 250 m  · Barthstr. │     │
│   └────────────────────┘     │
│                              │
│   ████████░░░░ 12.3km 15Min  │  ← Progress
│                              │
│                   [■][📍]    │  ← Stop + GPS
└──────────────────────────────┘
```

All HUD elements are **absolutely positioned** over the map. The map is always full-height and interactive (zoom/pan).

## Data Flow

```
NavService ──► Map.razor.cs (subscribes to events)
                  │
                  ├── ManeuverChanged    → _navManeuver (next turn)
                  ├── StatusUpdated      → _status (progress, distance, time)
                  ├── NavigationCompleted → Snackbar + back to Plan-Modus
                  ├── NavigationStateChanged → UI mode switch
                  └── BleCommandSimulated  → _bleLog (debug overlay)

Map.razor (UI) reads:
  - NavService.IsNavigating → decides Plan vs Navi mode
  - Gps.LastReading / CurrentSpeedKmh → speed + position
```

## Files Changed

### `Map.razor` (view)
- Wrap search/planning panel in `@if (!NavService.IsNavigating)`
- Add navigation HUD section `@if (NavService.IsNavigating)`
  - Top bar: maneuver counter, accuracy, BLE log button
  - Speed overlay
  - Next turn card
  - Progress bar + distance/time
  - Stop button
  - BLE log (collapsible, debug)
- OpenStreetMap + GPS FAB stay **outside** both conditionals (always visible)

### `Map.razor.cs` (code-behind)
- Add fields: `NavigationManeuverInfo? _navManeuver`, `NavigationStatus? _navStatus`, `double _navProgress`, `List<string> _bleLog`, `bool _showBleLog`
- OnInitialized: subscribe to NavService events
- Handlers: `OnManeuverChanged`, `OnStatusUpdated`, `OnNavigationCompleted`, `OnNavigationStateChanged`, `OnBleCommand`
- `StartNavigation()`: remove `NavManager.NavigateTo("/navigation")` — stays on `/map`
- Add method: `ExitNavigation()` — stop NavService, stop GPS, clear breadcrumb, back to Plan-Modus
- Dispose: unsubscribe NavService events

### `Navigation.razor` (unchanged)
- Kept as reference / fallback — not deleted

## Edge Cases & Errors

| Situation | Behavior |
|-----------|----------|
| GPS off when navigation starts | `StartNavigation()` already starts GPS tracking (`Gps.StartTrackingAsync`) |
| GPS lost mid-navigation | Last known position stays on map, speed shows 0, accuracy increases |
| Navigation completed | `NavigationCompleted` fires → Snackbar "Ziel erreicht!" → back to Plan-Modus |
| User presses Stop mid-navigation | Stop GPS, clear breadcrumb, back to Plan-Modus, route stays visible |
| BLE log overflow | Trimmed to last 200 entries (same as current Navigation.razor) |
