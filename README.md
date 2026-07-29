<div align="center">
  <img src="src/VegaBridgeApp/Resources/AppIcon/appiconfg.svg" width="120" height="120" alt="VegaBridge Logo">
  <h1>VegaBridge</h1>
  <p><strong>Motorcycle navigation for MV Agusta —<br>turn-by-turn on your bike's dashboard via BLE</strong></p>

  <p>
    <img src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&style=flat" alt=".NET 10">
    <img src="https://img.shields.io/badge/platform-iOS%20%7C%20Mac%20Catalyst-999999?logo=apple&style=flat" alt="Platform iOS | Mac Catalyst">
    <img src="https://img.shields.io/badge/license-MIT-green?style=flat" alt="License MIT">
  </p>
</div>

---

## Gallery

<table>
  <tr>
    <td width="25%"><img src="screenshots/01.png" alt="Map View"></td>
    <td width="25%"><img src="screenshots/02.png" alt="Navigation"></td>
    <td width="25%"><img src="screenshots/03.png" alt="My Routes"></td>
    <td width="25%"><img src="screenshots/04.png" alt="Settings"></td>
  </tr>
  <tr align="center">
    <td><strong>🗺️ Interactive Map</strong><br>With route planning & search</td>
    <td><strong>🧭 Navigation</strong><br>Turn‑by‑turn with live instructions</td>
    <td><strong>📋 My Routes</strong><br>Save, load, import & export GPX</td>
    <td><strong>⚙️ Settings</strong><br>BLE device & connection management</td>
  </tr>
</table>

---

## Features

- **🗺️ Interactive Map** — Powered by OpenLayers with smooth pan/zoom, route preview, and GPS breadcrumb trail.
- **🧭 Turn-by-Turn Navigation** — Routes calculated via [Valhalla](https://github.com/valhalla/valhalla) (OpenStreetMap data). Clear step‑by‑step instructions, distance, and ETA.
- **🏍️ BLE to Dashboard** — Connects to MV Agusta motorcycles via Bluetooth Low Energy. Sends turn instructions directly to the bike's dashboard using the proprietary MV Agusta protocol.
- **📍 Real‑Time GPS** — Background location tracking with Shiny.Locations. Accurate position on map and automatic navigation state machine.
- **💾 Route Management** — Save and load your favorite routes. Full [GPX](https://en.wikipedia.org/wiki/GPS_Exchange_Format) import and export support.
- **🌙 Dark Mode** — Adapts to system appearance automatically (iOS / macOS).
- **📱 Cross‑Platform** — Runs on iPhone, iPad, and Mac (Apple Silicon via Mac Catalyst).

---

## Tech Stack

| Area              | Technology                                                                   |
|-------------------|------------------------------------------------------------------------------|
| **Framework**     | .NET 10 · MAUI · Blazor Hybrid                                              |
| **UI**            | [MudBlazor](https://mudblazor.com/) · MudExtensions · CommunityToolkit.Maui |
| **Maps**          | [OpenLayers.Blazor](https://github.com/achavez99/OpenLayers.Blazor)         |
| **BLE**           | [Shiny.BluetoothLE](https://github.com/shinyorg/shiny)                      |
| **GPS**           | [Shiny.Locations](https://github.com/shinyorg/shiny)                        |
| **Routing**       | Valhalla (via `valhalla1.openstreetmap.de`) · Photon Geocoding               |
| **Logging**       | Serilog                                                                      |
| **HTTP**          | `IHttpClientFactory` with Polly resilience pipelines                         |

---

## Architecture

VegaBridge follows a **plugin architecture** for BLE communication:

```
BleManagerService          ← Central coordinator
  ├── MvAgustaBlePlugin   ← Handles MV Agusta protocol frames
  └── ...                 ← Future manufacturers (extensible via IBleDevicePlugin)
```

The `IBleDevicePlugin` interface allows supporting additional motorcycle brands with their own BLE service UUIDs, frame formats, and handshake sequences — no changes needed to the core service.

Navigation uses a **deterministic state machine** (`NavigationService`) that reacts to GPS position changes and triggers re‑routing when the rider deviates from the planned path.

---

## Building

```bash
# Clone the repository
cd VegaBridge

# Build for iOS Simulator (debug)
dotnet build src/VegaBridgeApp -f net10.0-ios

# Build for Mac Catalyst (debug)
dotnet build src/VegaBridgeApp -f net10.0-maccatalyst

# Release build for iOS (requires signing)
dotnet build src/VegaBridgeApp -f net10.0-ios -c Release

# Create IPA for App Store distribution
dotnet publish src/VegaBridgeApp -f net10.0-ios -c Release
```

> **Note:** Release builds require a valid Apple Distribution certificate and provisioning profile configured in the `.csproj` (`CodesignKey` / `CodesignProvision`).

---

## BLE Setup (iOS)

1. **Pair** your motorcycle in **Settings → Bluetooth** (iOS system pairing).
2. Open **VegaBridge → Settings** — your motorcycle appears automatically as "Gekoppelt".
3. Tap **Connect** — the app establishes the BLE data connection and starts the handshake.

The app uses the MV Agusta proprietary BLE protocol (service `00003719-...`).  
For protocol reverse‑engineering details, see [`ReverseEngineering/MVRide/docs/`](ReverseEngineering/MVRide/docs/).

---

## Project Structure

```
VegaBridge/
├── src/VegaBridgeApp/          # Main .NET MAUI app
│   ├── Components/             # Blazor pages & components
│   │   ├── Dialogs/            #   GPX import/export, rename dialogs
│   │   ├── Layout/             #   MainLayout (MudBlazor shell)
│   │   └── Pages/              #   Map, Navigation, MyRoutes, Settings
│   ├── Models/                 # Domain models (BLE, routes, geocoding)
│   ├── Resources/              # Localization (.resx), app icon, splash
│   ├── Services/               # Core services
│   │   ├── BLE/                #   BLE manager, plugin, delegate
│   │   ├── Location/           #   GPS tracking
│   │   ├── Navigation/         #   Navigation state machine
│   │   ├── Routes/             #   Route persistence & GPX conversion
│   │   ├── Valhalla/           #   Routing engine HTTP client
│   │   └── Geocoding/          #   Address search (Photon)
│   └── Platforms/              # iOS, Android, MacCatalyst config
├── ReverseEngineering/         # BLE protocol analysis
└── docs/                       # Design specs & planning
```

---

## License

MIT
