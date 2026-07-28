# VegaBridge

Motorcycle navigation app for MV Agusta motorcycles. Connects via Bluetooth Low Energy (BLE) and provides turn-by-turn navigation directly on the bike's dashboard.

## Features

- 🗺️ Interactive map (OpenLayers) with route planning
- 🧭 Turn-by-turn navigation with Valhalla routing engine
- 🏍️ BLE connection to MV Agusta dashboards for navigation display
- 📍 GPS tracking with breadcrumb trails
- 💾 Save and load routes (GPX import/export)
- 🌙 Dark mode support (system-dependent)
- 📱 iOS and Mac Catalyst support

## Tech Stack

- **.NET MAUI Blazor Hybrid** (net10.0-ios, net10.0-maccatalyst)
- **OpenLayers.Blazor** — map rendering
- **Shiny.Locations** — GPS tracking
- **MudBlazor** — UI components
- **Valhalla** — routing engine (via OpenStreetMap.de instance)
- **Photon (Komoot)** — geocoding / address search

## Building

```bash
cd src/VegaBridgeApp
dotnet build -f net10.0-ios              # Debug
dotnet build -f net10.0-ios -c Release   # Release (Distribution signing)
dotnet publish -f net10.0-ios -c Release # Create IPA for App Store
```

> **Note:** The Release build requires a valid Apple Distribution certificate and provisioning profile configured in the `.csproj`. See `VegaBridgeApp.csproj` → `CodesignKey` / `CodesignProvision`.

## BLE Setup (iOS)

1. Pair your motorcycle in **Settings → Bluetooth** (iOS system pairing)
2. Open VegaBridge → Settings → your motorcycle appears automatically as "Gekoppelt"
3. Tap **Connect** to start the BLE data connection

The app uses MV Agusta's proprietary BLE protocol (service `00003719-...`). For protocol details, see `ReverseEngineering/MVRide/docs/`.

## License

MIT
