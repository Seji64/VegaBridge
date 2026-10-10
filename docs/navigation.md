# Navigation Architecture

## Overview

VegaBridge provides turn-by-turn navigation on MV Agusta motorcycles by:
1. Calculating routes via Valhalla
2. Tracking the rider's GPS position along the route
3. Sending navigation instructions to the bike's TFT display via BLE

The system works with the screen OFF — the UI is only needed for optional glanceable updates. Core logic runs from GPS callbacks regardless of display state.

---

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                          UI Layer (Map.razor)                        │
│  Blazor Hybrid · Displays route, maneuvers, speed, remaining dist   │
└───────────────────────────────┬─────────────────────────────────────┘
                                │ implements INavigationSink
┌───────────────────────────────┴─────────────────────────────────────┐
│                      NavigationService (Core)                        │
│  GPS → EMA smoothing → Route snap → Off-route detection → Maneuvers │
│  Valhalla integration · Rerouting · Polyline densification          │
└───────────────────────────────┬─────────────────────────────────────┘
                                │ implements INavigationSink
┌───────────────────────────────┴─────────────────────────────────────┐
│                   BleNavigationCoordinator (Mediator)                │
│  Translates NavigationService events → BLE frames                   │
│  Throttling · Send gate serialization · Reconnect handling          │
└───────────────────────────────┬─────────────────────────────────────┘
                                │ uses
┌───────────────────────────────┴─────────────────────────────────────┐
│                     BleManagerService (Transport)                    │
│  Scanning · Connecting · Plugin selection · Reconnect logic          │
└───────────────────────────────┬─────────────────────────────────────┘
                                │ delegates to
┌───────────────────────────────┴─────────────────────────────────────┐
│                    MvAgustaBlePlugin (Protocol)                      │
│  Frame encoding (NAVI, SM, SM1, DEST, RENAVI, PING, FINISH)        │
│  Write-without-response · Keepalive · Session management            │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Data Flow

### 1. Route Calculation

```
User taps "Start Navigation" on Map
  → Rider somewhere along the route (not at its start)?
      → dialog: "Smart start" (skip waypoints already passed)
                or "From the beginning" (planned start = first waypoint)
  → Map.razor calls ValhallaClient.GetRouteAsync() (from the current position)
  → Route response (polyline, maneuvers, summary)
  → NavigationService.StartNavigation(shape, maneuvers, ...)
  → Polyline densification (max 20m segments)
  → Way-ID index built via ONE Valhalla /locate call
    (every ~1 km + middle of every maneuver)
  → GPS tracking started
  → INavigationSink.OnStartAsync() fired to all sinks
```

### 2. GPS Tick Loop (1 Hz)

```
GpsService.ReadingReceived → OnGpsReading()
  │
  ├─ GPS Accuracy Guard: accuracy > 40m → SKIP (hold state)
  │
  ├─ EMA smoothing (α=0.7, reset after a GPS gap > 3s)
  │
  ├─ Heading tracking (GPS course or buffer-derived)
  │
  ├─ Snap to route (SnapToRoute: window around the progress,
  │   100m back / 500m ahead – a later pass over the same road,
  │   e.g. a round trip's end at its start, is never matched early)
  │
  ├─ Off-Route Detection (Gated Topology)
  │   ├─ FAST PATH: XTE ≤ 20m (40m within 30m of a turn) → ON_ROUTE
  │   │   └─ Wrong-Way check: heading vs local route bearing > 135° (3 ticks)
  │   │
  │   └─ SUSPECT
  │       ├─ Wrong way OR XTE > 50m → off-route candidate (every tick, no API call)
  │       └─ else VerifyTopologyAsync (throttled 1/2s, raw GPS fix)
  │           └─ Valhalla /locate → way_id comparison
  │               ├─ Match → ON_ROUTE (4s locate cooldown)
  │               └─ No match → off-route candidate
  │       3 candidates → OFF_ROUTE → RENAVI + reroute
  │
  ├─ Maneuver tracking (advance index when snapped passes begin)
  │
  ├─ Distance calculations (to next turn, remaining)
  │
  └─ INavigationSink.OnStatusAsync() → BLE + UI
```

### 3. BLE Transmission

```
NavigationService fires OnStatusAsync()
  → BleNavigationCoordinator.SendUpdateAsync()
  → SemaphoreSlim gate (10s timeout)
  → MvAgustaBlePlugin.SendNavigationUpdateAsync()
  → Frame encoding: NAVI + SM + SM1
  → Write-without-response to characteristic 0x2345
  → PING keepalive every 15s
```

### 4. Off-Route → Reroute

```
Off-Route detected (3 candidates)
  → INavigationSink.OnOffRouteAsync()
      → BleNavigationCoordinator sends RENAVI frame
      → Map only shows a snackbar
  → NavigationService.PerformRerouteAsync() – started by the service itself
    (works without the map page / screen off), from the latest GPS fix
    and heading; retried every 30s while still off route
  → Valhalla /route API with the waypoints still ahead (passed = progress
    beyond the waypoint's leg end), all as "break"
  → Route replaced in-place
  → INavigationSink.OnRouteUpdatedAsync()
```

---

## Off-Route Detection: Gated Topology Matching

### Problem

GPS-to-polyline distance is unreliable for off-route detection:
- In curves, GPS deviates 15-40m from the polyline (chord error)
- Parallel roads 20m away show the same heading
- Simple distance thresholds cause constant false RENAVI

### Solution: Two-Layer Gated Architecture

**Layer 1: Fast Path (every GPS tick, no API cost)**
- Cross-Track Error (XTE) check against threshold
- Threshold: 20m, 40m within 30m (along the route) of a maneuver
- Wrong-way detection: heading vs local route bearing > 135°

If XTE ≤ threshold AND heading OK → ON_ROUTE. No API call.

Clear evidence counts as an off-route candidate on every tick, without an
API call: wrong way, or XTE > 50m. (A U-turn on the route road, or a wrong
turn onto the route's own street, would match the route's Way ID.)

**Layer 2: Slow Path (only for 20–50m, throttled)**
- Valhalla `/locate` API with the raw GPS fix → OSM Way ID at the position
- Compare against the route's Way IDs (built once per route; no verdict
  while the index is still being built)
- Throttled: max 1 API call per 2 seconds

If Way ID matches route → ON_ROUTE (no locate for 4s).
3 off-route candidates → OFF_ROUTE → RENAVI + reroute.

Throttles and cooldowns run on the GPS fix timestamps (≈ wall clock at 1 Hz).

### Edge Cases Handled

| Scenario | Detection |
|---|---|
| Hairpin curve (XTE 35m) | Fast path SUSPECT → locate confirms way_id → ON_ROUTE |
| GPS glitch (2 ticks off) | Hysteresis doesn't reach threshold → resets |
| Wrong way / U-turn | Heading divergence > 135° → off-route even at XTE 0m |
| Wrong turn in the city | Wrong way at the corner / XTE > 50m → reroute after ~5s (36 km/h) |
| Parallel road (≥ 20m, same heading) | Locate returns different way_id → reroute after ~6s (120 km/h) |
| Parallel road < 20m | Not detected – not separable by GPS |
| GPS drift on the route road (city) | Locate confirms way_id → ON_ROUTE |
| Tunnel / bad GPS (accuracy > 40m) | Tick ignored entirely, state frozen |
| Round trip / overlapping route | Progress window: later pass is not matched early |

### Cost Optimization

Without gating: 1 API call per second = 3600/hour.
With gating: ~95% of ticks pass fast path → ~180 API calls/hour max.
With cooldown: after way_id match, 4s pause → even fewer calls.
Wrong way and XTE > 50m need no API call at all.

### Telemetry

At ride end, the system logs:
```
OffRouteDetector: 847 ticks, 812 on-route, 35 suspect, 12 ignored (accuracy), 3 locate calls, 8 throttled
```

---

## Polyline Densification

Valhalla route polylines can have segments >100m in curves. The chord error (distance between the curved road and the straight polyline segment) causes false off-route detection.

**Fix:** After decoding the polyline, insert intermediate points so no segment exceeds 20m. Maneuver shape indices are remapped to the densified polyline.

```
Before: A ─────────────────── B  (100m segment, 30m chord error)
After:  A ── C ── D ── E ── F ── B  (20m segments, <5m chord error)
```

---

## BLE Protocol (MV Agusta)

### Frame Format

All frames follow: `0x0D <command> 0x1E <field1> 0x1E <field2> ... 0x0D`

### Commands

| Command | Direction | Purpose |
|---------|-----------|---------|
| `NAVI` | Phone → Bike | Navigation instruction (icon, text, street) |
| `SM` | Phone → Bike | Status/Motion (speed, remaining distance, turn distance) |
| `SM1` | Phone → Bike | Arrival time (minutes since midnight) + remaining minutes, every 30 s |
| `DEST` | Phone → Bike | Destination coordinates |
| `REM` | Phone → Bike | Remaining distance to destination |
| `RENAVI` | Phone → Bike | Off-route alert → bike shows rerouting |
| `FINISH` | Phone → Bike | Navigation ended (destination or cancel) |
| `PING` | Phone → Bike | Keepalive (every 15s) |
| `GUI1` | Bike → Phone | Session heartbeat (contains session ID) |

### Connection Lifecycle

```
Scan → Connect → Notifications enabled → NAVI/SM/PING loop
     ↓ (write failure)
  InvalidateConnectionAndReconnect (15s cooldown)
     ↓
  Reconnect → Resume PING → Resend current state
```

### Write Failures

iOS can silently drop BLE links while the app is in background. Signs:
- `CanSendWriteWithoutResponse: False` (buffer full)
- `GATT is not connected` (link dead)
- `Arg_TimeoutException` (write timeout)

Recovery: `InvalidateConnectionAndReconnect` with 15s cooldown to prevent reconnect storms.

---

## Maneuver Handling

### Display Logic

The displayed maneuver is NOT always the current one. If the current maneuver is "straight", the system skips ahead to the next non-straight maneuver (e.g., "turn left in 500m").

```
Maneuver 0: "Depart" (straight) → skip
Maneuver 1: "Turn right" (non-straight) → DISPLAYED
Maneuver 2: "Continue" (straight) → skip
Maneuver 3: "Turn left" (non-straight) → shown when 1 is passed
```

### Maneuver vs Instruction Timing

Valhalla maneuvers describe the action at their BEGIN index (the turn happens AT begin_shape_index). The instruction is shown BEFORE the turn, not during.

---

## Components Reference

| File | Role |
|------|------|
| `NavigationService.cs` | Core state machine: GPS processing, route tracking, off-route detection |
| `INavigationSink.cs` | Event interface for navigation state changes |
| `BleNavigationCoordinator.cs` | Mediator: translates nav events → BLE frame sends |
| `BleManagerService.cs` | BLE transport: scanning, connecting, reconnecting |
| `MvAgustaBlePlugin.cs` | MV Agusta protocol: frame encoding, keepalive, session |
| `ValhallaClient.cs` | Valhalla HTTP client: route, trace_route, locate |
| `GpsService.cs` | GPS tracking via Shiny.Locations |
| `DebugLogSink.cs` | In-memory Serilog sink for log export |
| `VegaBridgeApp.Tests/NavigationServiceTests.cs` | Simulated rides: round trip, wrong turn, U-turn, parallel road, GPS drift, reroute waypoints (`dotnet test src/VegaBridgeApp.Tests`) |

---

## Configuration

| Parameter | Default | Description |
|-----------|---------|-------------|
| `SuspectXteM` | 20m | XTE threshold for SUSPECT (straight segments) |
| `SuspectManeuverXteM` | 40m | XTE threshold near a maneuver |
| `TurnZoneM` | 30m | "Near a maneuver" (along the route) |
| `ClearlyOffRouteXteM` | 50m | Off-route candidate without /locate |
| `OffRouteConfirmCount` | 3 | Off-route candidates before RENAVI |
| `LocateThrottleSec` | 2s | Min interval between /locate API calls |
| `LocateCooldownSec` | 4s | No /locate after a way_id match |
| `RerouteMinIntervalSec` | 30s | Min interval between automatic reroutes |
| `SnapBackToleranceM` / `SnapForwardWindowM` | 100m / 500m | Snap window around the progress |
| `WrongWayHeadingThresholdDeg` | 135° | Heading divergence for wrong-way detection |
| `WrongWayMinSpeedMs` | 2.78 m/s | Min speed for wrong-way check (10 km/h) |
| `GpsSmoothingAlpha` | 0.7 | EMA weight for newest reading |
| `MapMatchBufferLimit` | 5 | GPS buffer size for map-matching |
| `Polyline densification` | 20m | Max segment length after decode |
| `PING interval` | 15s | BLE keepalive frequency |
| `InvalidateCooldown` | 15s | Reconnect storm prevention |
| `DebugLogSink capacity` | 20,000 lines | Ring buffer for log export |
