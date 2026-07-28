# App-Entscheidungen für MV Ride++ (Zusammenfassung)

## Architektur: Swift / Skip Fuse + Lite Hybrid

```
MVRideSkip/
├── MVRideCore/          # Business‑Logik, Routing‑Logik, Valhalla‑Wrapper (Skip Fuse – native Swift)
│   └── Skip/skip.yml          mode: native
├── MVRideBle/           # BLE‑Treiber (Skip Lite + #if SKIP für Android‑BLE)
│   └── Skip/skip.yml          mode: transpiled, bridging: true
├── MVRideUI/            # SwiftUI (Skip Fuse UI → SwiftUI auf iOS / Jetpack Compose auf Android)
│   └── Skip/skip.yml          mode: native
├── MVRideOffline/       # Offline‑Karten + Datenhaltung (Skip Fuse)
│   └── Skip/skip.yml          mode: native
├── Package.swift         # SwiftPM – definiert alle Module + Dependencies
├── Android/              # generiertes Gradle‑Projekt (nicht manuell editieren)
├── Darwin/               # Xcode‑Projekt‑Artefakte
└── docs/                 # Spezifikationen

Wichtige Dependencies (Package.swift):
  • skip-fuse-ui.git      → SkipFuseUI (SwiftUI‑Compose‑Brücke)
  • skip.git              → SkipStone (Build‑Plugin), SkipTest
  • skip-model.git        → SkipModel (Codable, @Observable)
```

---

## 1. Plattform- & Framework‑Wahl

| Kriterium | Swift / Skip Fuse (primär) + Lite‑BLE (sekundär) |
|-----------|--------------------------------------------------|
| **Sprache** | Swift (iOS‑nativ, Android via Skip‑Transpiler/Compiler) |
| **iOS‑UI** | SwiftUI (direkt, kein Overhead) |
| **Android‑UI** | Jetpack Compose (aus SwiftUI generiert) |
| **BLE auf iOS** | CoreBluetooth (direkt in Swift) |
| **BLE auf Android** | Android BLE API aus `#if SKIP`‑Blöcken (transpiliert zu Kotlin) |
| **Routing (Valhalla)** | Swift C‑Interop (Fuse) oder HTTP‑Wrapper |
| **Offline‑Karten** | `MapKit` (iOS), `Google Maps Compose` (Android) via `ComposeView` |
| **Background‑BLE (iOS)** | `bluetooth‑central` + `processing` Background‑Modes |
| **Background‑BLE (Android)** | Kotlin Foreground Service aus `#if SKIP`‑Block |
| **Build‑Workflow** | ✦ In **Xcode** entwickeln <br>✦ Skip‑Plugin baut Android automatisch mit <br>✦ Beide Simulatoren starten gleichzeitig |

**Fazit:** Swift/skip.dev ist die optimale Wahl, weil:
- Du iOS **und** Android aus einem Swift‑Codebase bedienst
- SwiftUI sich **eins‑zu‑eins auf iOS** verhält (kein MAUI‑Abstraktions‑Overhead)
- Android‑BLE per `#if SKIP` direkt als Kotlin angesprochen wird (kein C#‑BLE‑Plugin nötig)
- Du die komplette **Xcode‑Toolchain** nutzen kannst

---

## 2. Skip Fuse vs. Skip Lite

| Aspekt | Skip Fuse (native) | Skip Lite (transpiliert) |
|--------|-------------------|--------------------------|
| **Swift‑Compiler** | ✅ Vollständig – Swift 6, alle Sprachfeatures | ⚠️ Teilmenge (kein `defer`‑Mapping, einige Generics‑Patterns) |
| **App‑Größe** | ~60 MB mehr (Swift‑Stdlib + Foundation auf Android) | Schlanker (nur Skip‑Kompatibilitäts‑Libs) |
| **Kotlin/Java‑Integration** | ❌ Nur via Bridge / JNI / AnyDynamicObject | ✅ Direkt – transpilierter Code **ist** Kotlin |
| **C‑Interop** | ✅ `#if !SKIP` – direkt (wie auf iOS) | ⚠️ Umständlich (SkipFFI nötig) |
| **Build‑Zeit** | Langsamer (Cross‑Compile) | Schneller (nur Kotlin‑Compile) |
| **Debugging (Android)** | Schwierig (native .so) | Einfach (generiertes Kotlin in Android Studio) |
| **Empfehlung** | **Business‑Logik + Routing + UI** | **BLE‑Backend + Android‑spezifische API‑Aufrufe** |

**Hybrid‑Ansatz für MV Ride:**
- **MVRideCore** + **MVRideUI** → **Skip Fuse** (volle Swift‑Unterstützung, C‑Interop für Valhalla)
- **MVRideBle** → **Skip Lite** (`bridging: true`), damit Android‑BLE direkt in `#if SKIP`‑Blöcken aufgerufen werden kann
- Die Brücke zwischen Fuse‑ und Lite‑Modulen übernimmt Skip automatisch (via `bridging`)

---

## 3. Hintergrundbetrieb (Bildschirm aus, BLE weiter)

### iOS – Background‑Modes (Info.plist)
```xml
<key>UIBackgroundModes</key>
<array>
    <string>bluetooth-central</string>
    <string>location</string>
    <string>processing</string>
</array>
```

- `CoreBluetooth`‑Verbindung bleibt aktiv, solange die App nicht suspendiert wird.
- **Keep‑Alive** (`GUI1` alle ~3 s) verhindert Suspendierung.
- **GPS‑Updates** via `CLLocationManager` (`allowsBackgroundLocationUpdates = true`).
- **Auto‑Reconnect**: `CBCentralManager`‑Delegate `didDisconnectPeripheral` → erneuter Verbindungsversuch.

### Android – Foreground Service (in #if SKIP)
```kotlin
// Wird in Swift als #if SKIP-Block geschrieben – Skip transpiliert es zu Kotlin
#if SKIP
import android.app.Service
import android.bluetooth.BluetoothGatt
…

class BleForegroundService : Service() {
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startForeground(NOTIFICATION_ID, notification)
        // BLE‑Verbindung halten, GUI1‑Timer starten
        return START_STICKY
    }
}
#endif
```

Berechtigungen in `AndroidManifest.xml` (in `#if SKIP`‑Blöcken oder via `skip.yml` setzen):
```xml
<uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE_CONNECTED_DEVICE" />
<uses-permission android:name="android.permission.BLUETOOTH_CONNECT" />
<uses-permission android:name="android.permission.BLUETOOTH_SCAN" />
<uses-permission android:name="android.permission.ACCESS_BACKGROUND_LOCATION" />
```

**Testen:** LightBlue (iOS) / nRF Connect (Android) – prüfen, ob `GUI1` und `NAVI`‑Kommandos bei gesperrtem Bildschirm weiter ankommen.

---

## 4. BLE‑Kommunikation – Plattform‑spezifisch

| Plattform | API | In Skip‑Projekt |
|-----------|-----|-----------------|
| **iOS** | `CoreBluetooth` (CBCentralManager, CBPeripheral) | Direkter Swift‑Code in `MVRideBle` (Fuse‑Modul) |
| **Android** | `android.bluetooth` (BluetoothGatt, BluetoothAdapter) | `#if SKIP`‑Block im selben Modul → wird zu Kotlin transpiliert |

**Struktur von `MVRideBle/BleService.swift`:**

```swift
import Foundation

// Plattform‑unabhängige Schnittstelle
public protocol BleServiceProtocol {
    func connect(to deviceId: String) async throws
    func send(command: String, args: String...) async throws
    var onDataReceived: AsyncStream<Data> { get }
}

// iOS‑Implementierung (CoreBluetooth)
#if !SKIP
import CoreBluetooth

public actor BleService: NSObject, BleServiceProtocol {
    private var centralManager: CBCentralManager!
    private var peripheral: CBPeripheral?
    // …
}
#endif

// Android‑Implementierung (#if SKIP → transpiliert zu Kotlin)
#if SKIP
import android.bluetooth.*

public class BleService : BleServiceProtocol {
    private var bluetoothAdapter: BluetoothAdapter?
    private var gatt: BluetoothGatt?

    public func connect(to deviceId: String) {
        val device = bluetoothAdapter?.getRemoteDevice(deviceId)
        gatt = device?.connectGatt(/* … */)
    }
}
#endif
```

---

## 5. Navigation & Routing (Offline‑fähig)

### 5.1 Routing‑Engine Valhalla – Einbindung

| Methode | Beschreibung | In Skip‑Projekt |
|---------|-------------|-----------------|
| **C‑Interop** (empfohlen) | Valhalla als native C‑Bibliothek (`libvalhalla.a` / `.so`) | ✅ in Fuse‑Modulen: `#if !SKIP` – direkter C‑Funktionsaufruf |
| **HTTP‑Server** (einfacher) | Docker‑Container auf dem Gerät / lokal | ✅ `URLSession` (iOS) / `java.net.HttpURLConnection` (`#if SKIP`) |
| **AnyDynamicObject** (nur Android) | JNI‑Brücke zur `.so`‑Bibliothek | ⚠️ nur für Android, umständlich |

**Beispiel: C‑Interop (Skip Fuse)**

```swift
// MVRideCore/Valhalla/RoutingService.swift
#if !SKIP
import Foundation

// C‑Funktionen aus libvalhalla
@_silgen_name("valhalla_route")
func valhalla_route(_ request: UnsafePointer<CChar>) -> UnsafeMutablePointer<CChar>?

@_silgen_name("valhalla_free")
func valhalla_free(_ ptr: UnsafeMutablePointer<CChar>?)

public struct RoutingService {
    public static func getRoute(start: (Double, Double), end: (Double, Double)) throws -> [Maneuver] {
        let jsonRequest = """
        {"locations":[{"lat":\(start.0),"lon":\(start.1)},{"lat":\(end.0),"lon":\(end.1)}],"costing":"motorcycle"}
        """
        let responsePtr = jsonRequest.withCString { valhalla_route($0) }
        defer { valhalla_free(responsePtr) }
        let responseJson = String(cString: responsePtr!)
        return try JSONDecoder().decode(ValhallaResponse.self, from: Data(responseJson.utf8)).maneuvers
    }
}
#endif
```

**Daten‑Footprint:** ≈ 2 GB (Valhalla‑Tiles ≈ 1,2 GB + OpenMapTiles‑MBTiles ≈ 500 MB für Frankreich).

### 5.2 Turn‑By‑Turn → BLE‑Mapping

```swift
public enum TurnType: Int {
    case turnLeft = 1, turnRight = 2, roundaboutLeft1 = 12, /* … 34 Werte aus TurnByTurnIndication.java */ …
}

public struct Maneuver: Codable {
    let type: TurnType
    let distanceMeters: Double
    let instruction: String
}

// Mapping der Valhalla‑Maneuver‑Typen auf die MV‑Ride‑TurnCodes
func mapValhallaManeuver(_ valhallaType: String, _ distance: Double) -> (turnCode: Int, distance: String) {
    // "turn-left" → 1, "turn-right" → 2, "roundabout-left-2" → 13, …
}
```

### 5.3 Karten‑UI

```swift
struct MapView: View {
    var body: some View {
        #if os(Android)
        ComposeView { MapComposer() }
        #else
        Map()
        #endif
    }
}

#if SKIP
import com.google.maps.android.compose.*

struct MapComposer: ContentComposer {
    @Composable func Compose(context: ComposeContext) {
        GoogleMap(/* … */)  // offline‑MBTiles via Google Maps TileOverlay
    }
}
#endif
```

---

## 6. Offline‑Daten

| Daten | Format | Speicherort | Grösse |
|-------|--------|-------------|--------|
| **Valhalla‑Routing‑Tiles** | `.gph` (komprimiert) | `AppData/valhalla/tiles/` | ~1,2 GB (Frankreich) |
| **Karten‑Tiles (Vektor)** | `.mbtiles` (SQLite) | `AppData/tiles/france.mbtiles` | ~500 MB |
| **GPX‑Import** | `.gpx` | `AppData/imports/` | < 1 MB |
| **BLE‑Protokoll‑Referenz** | n/a (im Code) | – | – |

**Download‑Manager:** Der Nutzer lädt beim ersten Start (oder bei Bedarf) die Region‑Pakete. Skip kann das über `URLSession` (Download‑Task im Background‑Mode) abwickeln.

---

## 7. Nachrichten‑Command (`MSG`)

Android kann via **NotificationListenerService** (wie im KTM‑Nav‑GEN3‑Repo) WhatsApp/Telegram etc. abgreifen → `MSG`‑Kommando.

iOS hat **keinen öffentlichen Zugriff** auf Benachrichtigungen anderer Apps. Daher:
- `MSG` auf iOS nur über **eigene UI** (z. B. Quick‑Reply‑Fenster) oder
- Gar nicht senden → Bike zeigt dann nur die Navigations‑Infos.

```swift
#if SKIP
// Android: NotificationListener → MSG‑Frame bauen
let appId = when(packageName) {
    "com.whatsapp" -> "whatsapp"
    "org.telegram.messenger" -> "telegram"
    // …
}
let frame = "\rMSG\u{1E}\(appId)\u{1E}\(message)\u{1E}\(title)\r"
bleService.send(frame.data(using: .utf8)!)
#endif
```

---

## 8. Zusammenfassung aller Requirement‑Erfüllungen

| ✅ | Anforderung | Erfüllung in Swift/skip.dev |
|----|------------|----------------------------|
| 1 | **Entwicklung in einer Sprache** | Swift – iOS + Android aus einem Codebase |
| 2 | **Offline‑Navigation** | Valhalla via C‑Interop (Fuse) + offline MBTiles |
| 3 | **Daten‑Footprint ~2 GB** | Valhalla‑Tiles 1,2 GB + Karten‑Tiles 500 MB |
| 4 | **BLE‑Hintergrund (Screen‑Off)** | iOS: `bluetooth‑central` + `processing` / Android: Foreground Service (`#if SKIP`) |
| 5 | **Turn‑By‑Turn an Bike** | `NAVI`, `SM`, `SM1`‑Kommandos aus Valhalla‑Maneuver‑Mapping |
| 6 | **Karten‑UI offline** | iOS: MapKit / Android: Google Maps Compose (offline‑Tiles) |
| 7 | **GPX‑Import** | GPX‑Parser in Swift (Foundation `XMLParser`) |
| 8 | **Keep‑Alive** | `GUI1`‑Timer in `BleService` (plattform‑unabhängig) |
| 9 | **Android‑MSG (WhatsApp etc.)** | NotificationListener → `#if SKIP` → MSG‑Frame |
| 10 | **App‑Store‑konform** | Alle APIs sind public; Background‑Modes begründet |
| 11 | **Ejectable** | iOS bleibt reines SwiftUI; Android behält generiertes Kotlin |

---

## 9. Entscheidungs‑Tree für die nächsten Schritte

```
Entscheidung:
  Swift für MV Ride-App?
      │
      ├── Ja → skip.dev einrichten
      │         ├─ brew install skip
      │         ├─ skip checkup
      │         └─ skip init --native-app --appid=com.mvride.MVRide MVRide MVRideUI MVRideBle
      │
      ├── Offline-Routing?
      │     ├── Ja → Valhalla-Tiles bauen (Docker)
      │     │         └─ In Fuse-Modul per C-Interop einbinden
      │     └── Nein → Google Maps Directions API (online)
      │
      ├── BLE-Hintergrund?
      │     ├── iOS: Info.plist + CoreBluetooth
      │     └── Android: #if SKIP-Block → Foreground Service
      │
      └── App testen
            ├─ skip app launch (iOS-Sim + Android-Emu gleichzeitig)
            └─ skip test (XCTest + JUnit Parity)
```

---

## 10. Änderungsprotokoll

| Datum | Version | Änderungen |
|-------|---------|------------|
| 2026-07-16 | v1.0 | Erstfassung (C#/.NET MAUI) basierend auf `mvride_nav.pklg`‑Capture |
| 2026-08-27 | v2.0 | APK‑Dekompilierung (jadx) – alle 16 Nachrichtentypen, UUIDs, Turn‑Enum, Pairing‑Mechanismus |
| 2026-08-27 | v3.0 | Ergänzung Abschnitt 13: Weiteres Vorgehen mit LightBlue (iOS) |
| 2026-08-27 | v4.0 | Umstellung von .NET MAUI auf **Swift / skip.dev** – Fuse+Lite‑Hybrid, Valhalla‑C‑Interop, BLE‑Hybrid‑Architektur |
| **2026-08-27** | **v5.0** | **Vollständige Überarbeitung für skip.dev** – Architektur, Module, BLE‑Aufteilung, Routing, Background‑Modes |

---

**Fazit:**  
Mit **Swift + skip.dev** (Skip Fuse für Logik/UI, Skip Lite für Android‑BLE) bekommst du eine Single‑Codebase‑App, die auf iOS **echtes SwiftUI** und auf Android **echtes Jetpack Compose** rendert. Die BLE‑Kommunikation läuft im Hintergrund (iOS und Android), Valhalla liefert offline‑Motorrad‑Routing mit ~2 GB Daten, und das gesamte Projekt bleibt in einer Sprache – Swift. Die alternative Plattform‑/Routing‑Architektur und die BLE‑Protokolldetails bleiben in `docs/ble-protokoll-spezifikation.md` weiterhin gültig.
