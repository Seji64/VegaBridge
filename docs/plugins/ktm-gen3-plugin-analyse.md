# Plugin-Konzept-Check: Könnte VegaBridge ein KTM-Gen-3-Plugin tragen?

Stand: 2026-10-14. Referenz: [Pavanayi1/KTM-Nav-GEN3](https://github.com/Pavanayi1/KTM-Nav-GEN3) (Commit `ceafaf1`, MIT-Lizenz),
eine Android-App (Kotlin), die das „BCCU“-BLE-Protokoll der Gen-3-Dashboards von KTM und Husqvarna implementiert.

Ziel dieses Dokuments:

1. Prüfen, ob sich mit `IBleDevicePlugin` / `IBleConnectedDevice` und den bestehenden Services ein KTM-Plugin bauen ließe,
   ohne den Kern umzubauen. Konkret: Was fehlt im Interface, wo sind Services nicht generisch?
2. Herausfinden, was VegaBridge (iOS, eigene Valhalla-Navigation, Dashboard-first) von der App übernehmen kann.

---

## TL;DR

- **Die Navigations-Semantik passt gut.** Start/Update/Finish/Stop/OffRoute/KeepAlive lassen sich 1:1 auf KTM abbilden, und die
  Regel „jeder Tick schickt den vollständigen Zustand“ ist ein guter, herstellerneutraler Vertrag.
- **Verbindung und Transport passen nicht.** In der heutigen Form kann man das KTM-Plugin **nicht** bauen, weil drei Dinge
  fehlen:
  1. ein **Lebenszyklus-Hook nach dem Verbindungsaufbau** (KTM braucht einen AES-Handshake, bevor das Dashboard etwas anzeigt),
  2. **Rückkanal-Writes aus `OnDataReceived`** und die **Characteristic-UUID** der eingehenden Daten (der Handshake ist ein
     Ping-Pong auf zwei Characteristics),
  3. **Write mit Response** (`IBleConnectedDevice` und der iOS-Schreibpfad kennen nur Write-without-Response).
- Daneben gibt es mehrere Stellen, die heute **MV-spezifisch im generischen Code** stecken. Die wichtigsten: der
  Wegpunkt→„straight“-Hack im Coordinator, „Connected“ bedeutet „bereit“, der W2R-Workaround für alle Plugins, ein grobes
  Icon-Set ohne Fahrseite.
- **MV profitiert von denselben Änderungen.** Die offizielle MV-Ride-App macht laut unserer Spec ebenfalls einen
  HELLO/NEED-Handshake, schickt `GUI1 00` beim Connect und bekommt Telemetrie (Tank, Batterie, Temperaturen, Fahrmodus,
  Modell-ID) über `GUI1`. Ein Init-Hook und ein Event-Rückkanal sind also keine KTM-Sonderwünsche.
- Was wir **übernehmen sollten** (alles iOS-tauglich): Symbol-Test-Seite für Dashboard-Icons, GATT-Dump und Modell-Erkennung im
  Log, Ellipsen-Kürzung, Display-Clear-Semantik, Tempolimit-Warnung über Valhalla `edge.speed_limit`, optional
  Stereo-Abbiegepiepen und Straßensperren als Dashboard-Meldung.

---

## Stand der Umsetzung

**Schritt 1 (Interface-Umbau) ist umgesetzt.** Damit sind alle Blocker B1–B5 und W1 erledigt, W4 teilweise:

| Punkt | Umsetzung |
|---|---|
| B1 Init-Hook + „bereit“ | `IBleDevicePlugin.InitializeSessionAsync(device, ct)` läuft nach **jedem** Link-Up. Der neue Zustand `BleConnectionState.Initializing` gilt, bis das Plugin `true` liefert, erst dann folgt `Connected`. Der Coordinator schickt den Session-Start erst bei `Connected`, Frame-Writes gehen nur an eine bereite Session. |
| B2 Empfangspfad | Das Plugin abonniert selbst: `IBleConnectedDevice.Subscribe(service, characteristic, onData)` pro Characteristic. Aus dem Callback kann es über dieselbe Session schreiben. `OnDataReceived` und `ReadCharacteristicUuid` sind entfallen. |
| B3 Multi-Service/-Characteristic | `WriteAsync(service, characteristic, data, withResponse)` und `Subscribe(...)` nehmen Service und Characteristic frei entgegen. |
| B4 Write mit Response | `withResponse: true` läuft über Shiny und wartet auf die Bestätigung. Nur `withResponse: false` nutzt den iOS-W2R-Pfad. |
| B5 Session-Ende | `IBleDevicePlugin.OnSessionEnded()` bei Linkverlust, User-Disconnect oder Ablösung durch einen neuen Link-Up. Eine beendete Session liefert keine Daten mehr, Writes werfen, der Init-Token ist abgebrochen. |
| W1 Geräte-ID | `IBleConnectedDevice.DeviceId` |
| W2 Link-Reset | anders gelöst: Liefert `InitializeSessionAsync` `false`, trennt der Manager den Link (`RecycleLink`), und der nächste Link-Up startet frisch. Das Plugin braucht dafür keine eigene Methode. |
| W4 Zustände | `Initializing` existiert, die UI zeigt in dieser Phase „Verbinde…“. Ein eigener Hinweis „am Dashboard bestätigen“ braucht den Event-Kanal (W3). |

**Schritt 2 (semantische Icons und Fahrseite) ist umgesetzt:**

- Neue Keys `keep-left/right`, `ramp-left/right` (getrennt von `exit-*`), `merge-left/right`, `ferry` und `waypoint`.
  Ob eine Ankunft Wegpunkt oder Ziel ist, entscheidet jetzt der Mapper
  (`NavigationIconMapper.GetSemanticIcon(maneuver)`), der Hack im Coordinator ist weg.
- Die Fahrseite kommt aus dem bestehenden `trace_attributes`-Aufruf (`edge.drive_on_right`, am öffentlichen
  Valhalla-Server geprüft: UK `false`, DE `true`). Sie steht als `NavigationStatus.DriveOnRight` am angezeigten
  Manöver zur Verfügung (`null`, solange unbekannt) und als `NavigationUpdateInput.DriveOnRight` im Plugin (Default
  Rechtsverkehr).
- MV: Kreisverkehr bei Linksverkehr → `roundabout-left-N`. Ankunft mit Seite (`kDestinationRight/Left`) ist am Ziel jetzt
  `Finish` (vorher Abbiegepfeil), an Zwischenzielen `straight` (vorher Abbiegepfeil). Alles andere bleibt, wie es war.
- HUD: eigene Icons für Spur halten (Gabel), Einfädeln, Fähre und Wegpunkt. Bei Linksverkehr zeigt der Kreisverkehr
  den gespiegelten Pfeil.
- `depart` und `keep-straight` aus der Skizze in Abschnitt 5 sind **entfallen**: `IsSkippableForDisplay` überspringt
  diese Manöver (wie Merge ohne Richtung und `kDestination`-Zwischenziele), sie erreichen also nie ein Display.

**Noch offen**, bewusst erst mit dem ersten Nutzer:

- W3 Event-Rückkanal: kommt mit dem ersten Konsumenten, z. B. MV-`GUI1`-Modellerkennung oder KTM-Tasten.
- W5 Marke pro Gerät, W6 Advertisement-Daten, W7 `SendMessageAsync`, Capabilities.
- `IsSkippableForDisplay` überspringt nur `kDestination`-Zwischenziele, nicht `kDestinationRight/Left`. Die offizielle
  MV-App springt bei jedem Zwischenziel zum übernächsten Manöver, das wäre ein eigener Schritt (betrifft auch das HUD).

Das MV-Plugin verhält sich unverändert: Es abonniert im Init die Read-Characteristic, macht keinen Handshake und
schreibt weiter ohne Response. Shiny-Abos werden wie bisher erst beim nächsten Link-Up ersetzt, nicht schon beim
Linkverlust. Contract-Tests liegen in `src/VegaBridgeApp.Tests/MvAgustaBlePluginTests.cs`.

---

## 1. Steckbrief: Was das KTM-BCCU-Protokoll braucht

| Thema | KTM Gen-3 (BCCU) | MV Agusta (heute in VegaBridge) |
|---|---|---|
| Services | `71ced1ac-0700-…` (Main/Nav), `…-0100` (Lenkertasten), `…-0000` (VIN), `…-0600` (Telemetrie-RPC), `180A` (Device Info) | ein Service `00003719-…` |
| Characteristics | eine **pro Display-Feld**: `0703` NAV_STATE, `0704` TURN_ICON, `0705` TURN_DISTANCE, `0706` TURN_INFO, `0707` TURN_ROAD, `0708` ETA, `0709` REMAINING_DISTANCE, `070a` NOTIFICATION | eine Write- und eine Read-Characteristic, Text-Frames (`\rCMD\x1e…\r`) |
| Write-Typ | **mit Response** (`WRITE_TYPE_DEFAULT`) | **ohne Response** |
| Sicherheit | App-Level-Handshake (Nonce-Tausch → HELLO → GENERATE_KEYS → Key-Select) und danach **jeder Write AES-CBC-verschlüsselt** mit Session-Key | keine |
| Persistenz | Pool aus 16 Session-Keys **pro Gerät** muss die Zündung überleben (sonst bei jedem Start „Gerät hinzufügen“ am Dash) | keine |
| Erstkopplung | Dash in „Add device“-Modus, Fahrer bestätigt **am Dash** (~30 s, Timeout 75 s) | keine |
| Payload-Größe | z. B. TURN_ROAD: 1 + ≤32 Zeichen, nach Framing und AES ≥ 64 Byte → größere MTU bzw. Long Write nötig | NAVI-Frames, 3 Link-Layer-Pakete |
| Start | `NAVIGATION_STATE guidanceOn=1` (ohne das rendert der Dash nichts) | `DEST` + `REM` |
| Update | `TURN_ICON` + `TURN_DISTANCE` („110 m“, max. 8) + `TURN_ROAD` (max. 32) + `ETA` („HH:MM“, **strikt ohne am/pm**) + `REMAINING_DISTANCE` (max. 8), optional `TURN_INFO` (max. 16) | `NAVI` + `SM` (+ `SM1` alle 30 s) |
| Ende | jedes Feld mit Sichtbarkeit `OFF` leeren, Icon `UNDEFINED` | `FINISH` |
| Off-Route | Banner `NOTIFICATION` mit Icon `REROUTING` | `RENAVI` |
| Keepalive | keiner | `PING` |
| Bike → App | Lenkertasten (SET/BACK/UP/DOWN), VIN, Modell/Firmware (180A); Telemetrie existiert, Writes werden aber abgelehnt | `GUI1`-Stream (Tank, Batterie, Temperaturen, Fahrmodus, Modell-ID), `NEED` |
| Icons | 58 Codes: LIGHT/QUITE/HEAVY links/rechts, KEEP_LEFT/MIDDLE/RIGHT, ENTER/LEAVE_HIGHWAY je Spur, START, END, FERRY, **Kreisverkehr Ausfahrt 1–16 getrennt nach Rechts- und Linksverkehr** | 35 Keys, Kreisverkehr 1–12 links/rechts |

Wichtige Fallstricke aus der KTM-App:

- Von 8 Banner-Icons rendert der Dash nur `NOTIFICATION_REROUTING` und `NOTIFICATION_WAYPOINT`. `TARGET_REACHED`
  und `GPS_LOST` bleiben unsichtbar (Hardware-Kalibrierung, siehe `AppNotificationListener.kt`).
- Zum Leeren nicht Icon `UNKNOWN(0)` schicken, sondern das letzte gültige Icon mit Sichtbarkeit `OFF` (Byte `1`).
- Nach dem Einschalten der Zündung funkt der Dash, bevor sein Pairing-Manager bereit ist. Die App wartet deshalb 10 s
  zwischen „gesehen“ und „verbinden“ und bricht hängende Handshakes nach 25 s ab.

---

## 2. Was am Konzept schon trägt

- **Semantische Navigationsmethoden** (`src/VegaBridgeApp/Services/BLE/IBleDevicePlugin.cs`). Abbildung auf KTM:

  | Interface | KTM |
  |---|---|
  | `SendNavigationStartAsync` | `NAVIGATION_STATE(guidanceOn, gpsIconOn)`; Zielkoordinaten werden ignoriert |
  | `SendNavigationUpdateAsync` | TURN_ICON/DISTANCE/ROAD/ETA/REMAINING, intern nur geänderte Felder schreiben |
  | `SendNavigationFinishAsync` | Turn-Icon `END`, danach alle Felder auf `OFF` |
  | `SendNavigationStopAsync` | alle Felder auf `OFF` |
  | `SendOffRouteAlertAsync` | Banner `REROUTING` |
  | `SendKeepAliveAsync` | No-op (laut Doku bereits erlaubt) |
  | `SendTestAsync` | Banner oder Begrüßung auf der Road-Zeile |

- **Vollständiger Zustand pro Tick** als Vertrag. Das KTM-Plugin kann intern diffen und nur geänderte Characteristics
  schreiben. Am Coordinator muss sich dafür nichts ändern.
- **Plugin-Auswahl über Name und Fallback auf die GATT-Service-UUID** (`BleManagerService.SelectPluginByServiceUuidAsync`).
  Der KTM-Dash heißt z. B. `KTM3638`, der Main-Service ist `71ced1ac-0700-44f5-9454-806ff70b3e02`.
- **Auto-Connect auf das letzte Gerät** und das **Re-Setup in `WhenConnected`** sind generisch. Sie müssten künftig nur
  zusätzlich die Plugin-Initialisierung (den Handshake) neu anstoßen.
- **Registrierung über DI** ist trivial: `AddSingleton<IBleDevicePlugin, KtmBlePlugin>()`.
- **Krypto ist reines .NET.** AES-CBC mit `PaddingMode.None` und SHA-512 aus `System.Security.Cryptography` laufen auf iOS
  ohne native Abhängigkeiten.

---

## 3. Lücken im Plugin-Interface

### 3.1 Blocker (ohne diese Punkte kein KTM-Plugin)

| # | Problem | Wo | Warum KTM scheitert | Nutzen für MV |
|---|---|---|---|---|
| B1 | **Kein Lebenszyklus nach dem Connect.** Es gibt keinen Hook „Link steht, jetzt Session aufbauen“ und kein Signal „Session bereit“. | `IBleDevicePlugin` | Der Handshake (Nonce, HELLO, Key-Select) muss nach jedem Link-Up laufen, auch nach Shiny-Auto-Reconnect. Erst danach sind Writes sinnvoll. | Die offizielle MV-App schickt `HELLO`, wartet auf `NEED`, macht nach 15 s bis zu 3 Retries und trennt sonst. Dazu kommt `GUI1 00` beim Connect. VegaBridge lässt das heute weg. |
| B2 | **`OnDataReceived(byte[])` kennt weder Characteristic noch Gerät.** | `IBleDevicePlugin.cs:44`, Aufruf in `BleManagerService.cs:781` | Auth-Nachrichten (`0701`) und Tasten (`0103`) kommen auf verschiedenen Characteristics. Der Handshake muss **aus dem Empfangspfad heraus antworten**, also schreiben. | auf `NEED`/`IOV` aus dem Empfangspfad reagieren (der genaue NEED-Ablauf ist laut Spec noch nicht ganz geklärt) |
| B3 | **Nur eine Notify-Characteristic** (`ReadCharacteristicUuid`), nur **ein Service** für Writes (`plugin.ServiceUuid`). | `IBleDevicePlugin.cs:29,34`, `BleManagerService.cs:763ff`, `:924` | Abonniert werden muss `0701` (Indicate) sofort, nach der Auth `0103` (anderer Service `0100`) und `070b`. Der VIN-Request geht an Service `0000`. | – |
| B4 | **Nur Write-without-Response.** Der iOS-Pfad schreibt fest mit `CBCharacteristicWriteType.WithoutResponse`, der Shiny-Pfad mit `withResponse: false`. | `IBleConnectedDevice.cs:15`, `BleManagerService.cs:524`, `:929` | KTM schreibt mit Response. CoreBluetooth verwirft WNR-Writes auf Characteristics ohne diese Property (API-Misuse-Warnung). Mit Response bekommen wir unter iOS zudem Long Writes für die ≥ 64-Byte-Payloads. | – |
| B5 | **Kein Disconnect-Hook.** Ein Singleton-Plugin mit Session-Zustand erfährt nicht, dass der Link weg ist. | `IBleDevicePlugin` | Die KTM-Logik erkennt `m1` daran, dass noch kein temporärer IV existiert. Bleibt der alte IV nach einem Linkabbruch stehen, wird das neue `m1` als verschlüsselte Control-Nachricht behandelt und der Handshake hängt. | SM1-Timer und Kreisverkehr-Cache sauber zurücksetzen |

### 3.2 Wichtig (für ein ordentliches KTM-Erlebnis)

| # | Problem | Wo | Details |
|---|---|---|---|
| W1 | **Keine Geräte-ID und kein Speicher pro Gerät im HAL.** | `IBleConnectedDevice` | Der Session-Key-Pool muss pro Dash persistiert werden. Das Plugin braucht eine stabile ID (`peripheral.Uuid`) und idealerweise einen `SecureStorage`-Wrapper (Keychain überlebt unter iOS sogar eine Neuinstallation). |
| W2 | **Plugin kann keinen Link-Reset anfordern.** | `IBleConnectedDevice` | KTM: ein hängender Handshake braucht einen sauberen Link-Drop. `ReconnectStalledLink` existiert schon, aber nur intern und nur für iOS-W2R (`BleManagerService.cs:534`). |
| W3 | **Kein Rückkanal vom Plugin zur App.** `OnDataReceived` ist `void`. | `IBleDevicePlugin` | KTM: Lenkertasten, Modell/Firmware/VIN. **MV:** `GUI1` mit Tankstand, Batterie, Temperaturen, Fahrmodus und `MODE_BIKE_ID` (Modell-String!). Damit könnte VegaBridge das MV-Modell automatisch ins Log schreiben, passend zum „Tell us if it works“ im README. |
| W4 | **Verbindungszustände sind zu grob** (`Idle/Scanning/Connecting/Connected/Error`). | `Models/BLE/BleConnectionState.cs` | Es fehlen „Session wird aufgebaut“ und „Bitte am Dashboard bestätigen“ (Erstkopplung bei KTM bis 75 s). Die UI würde heute „Verbunden“ zeigen, obwohl der Dash noch nichts annimmt. |
| W5 | **`BrandName` ist pro Plugin statisch.** | `IBleDevicePlugin.cs:19` | Ein BCCU-Plugin bedient KTM **und** Husqvarna, die Marke hängt also am Gerät. Vorschlag: `string? GetBrandName(BleDeviceInfo)`, oder `IsCompatible` liefert die Marke mit. |
| W6 | **`BleDeviceInfo` hat nur Name und UUID.** | `Models/BLE/BleDeviceInfo.cs` | Namensheuristiken kollidieren leicht (MV matcht auf `Contains("MV")`). Advertisement-Daten (Service-UUIDs, Manufacturer Data) aus dem Scan wären robuster. Shiny liefert sie, `ScanForUniquePeripherals` verwirft sie aber. |
| W7 | **Keine generische Text-Nachricht ans Dashboard.** | `IBleDevicePlugin` | Beide Marken können Text anzeigen (MV `MSG`, KTM Banner mit 16 Zeichen). Sinnvoll für **app-eigene** Hinweise wie Straßensperren voraus, Wegpunkt erreicht oder GPS verloren. Heute nutzt nur `SendTestAsync` diesen Kanal. |

### 3.3 Nice-to-have

- **Capabilities** des Plugins, z. B. `SupportsMessages`, `SupportsButtons`, `KeepAliveInterval`, `PreferredRefreshInterval`,
  `MaxRoundaboutExit`. UI und Coordinator können damit Features ein- oder ausblenden, statt MV-Kadenzen fest einzubauen.
- **Symbol-Test**: `IReadOnlyList<string> SupportedIcons` und `SendIconTestAsync(device, iconKey)`, siehe Abschnitt 6.
- **Pairing-Hinweis** als Resx-Key, z. B. „Am Dashboard Konnektivität → Gerät hinzufügen wählen“.

---

## 4. Nicht generische Stellen in den Services

| Stelle | Was MV-spezifisch ist | Vorschlag |
|---|---|---|
| `BleNavigationCoordinator.cs:310`: Wegpunkt-Ankunft (`ValhallaType == 4` mitten in der Route) wird zu `straight` umgebogen | Workaround, weil das MV-Display sonst FINISH zeigt. KTM hätte dafür ein eigenes Icon bzw. Banner (`NOTIFICATION_WAYPOINT`). | Neues semantisches Icon `waypoint`. Das MV-Plugin mappt es auf `straight`, KTM auf Banner plus `GO_STRAIGHT`. |
| `BleNavigationCoordinator.cs:129`: `OnLinkUpAsync` feuert bei `Connected` | „Link steht“ wird mit „Session bereit“ gleichgesetzt. Bei KTM ginge der Start ins Leere, weil die Auth noch läuft. | Auf einen neuen Zustand `Ready` triggern, den das Plugin nach erfolgreichem Init meldet (siehe B1/W4). |
| `BleNavigationCoordinator.cs:71`: PING-Schleife mit 15 s und 5-s-Skip nach NAVI | MV-Kadenz fest im Coordinator | Funktioniert dank No-op auch für KTM, sauberer wäre aber `KeepAliveInterval` als Capability (`null` = keine Schleife). |
| `BleNavigationCoordinator.cs:28`: 900-ms-Throttle mit Vollzustand | passt für MV-Frames | Für KTM okay, wenn das Plugin diffed. Optional als Capability. |
| `BleManagerService.cs:451ff`: W2R-Flusskontrolle (Drop, Force, Watchdog) gilt für **alle** Plugins | iOS-Workaround für Write-without-Response auf dem MV-Dash | Nur für WNR-Writes nutzen. Writes mit Response laufen über Shiny `WriteCharacteristicAsync(withResponse: true)`, das ein Ergebnis liefert. |
| `BleManagerService.cs:763ff`: `SetupNotifications` abonniert genau eine Characteristic | siehe B3 | Das Plugin abonniert im Init-Hook selbst über den HAL. |
| `NavigationIconMapper` | Rampen (17–19) und Ausfahrten (20/21) beide → `exit-*`; Spur halten (23/24) → `slight`; Merge/Fähre/Start → `straight`; keine Fahrseite | siehe Abschnitt 5 |
| `MvAgustaBlePlugin.cs:268`: Kreisverkehr immer `roundabout-right-N` | Rechtsverkehr fest verdrahtet, obwohl MV auch `roundabout-left-1…12` kennt | `DrivingSide` in `NavigationUpdateInput`. Hilft MV in UK/IE **und** KTM (`RAB_SECT_n_LH`). |
| `NavigationUpdateInput` | nur Rohwerte, keine Ankunftszeit, kein GPS-Status | `EstimatedArrival` (`DateTimeOffset`, eine Quelle für HUD und Dash statt `DateTime.Now + Rest` im Plugin), `IsPositionEstimated` (aus `NavigationStatus.IsEstimated`), `DrivingSide`, optional `RemainingWaypoints` |
| README: „no changes needed to the core service“ | stimmt für KTM nicht | nach dem Refactoring wieder wahr machen |

---

## 5. Vorschlag: semantische Icons erweitern

Valhalla liefert die Information bereits, sie wird nur im Mapper zusammengefasst. Vorschlag: zusätzliche semantische
Keys, die das MV-Plugin wieder auf seine 35 Keys herunterbricht.

| Valhalla-Typ | heute | neu (semantisch) | MV-Mapping | KTM-Mapping |
|---|---|---|---|---|
| 1 kStart, 2/3 kStartRight/Left | straight / turn-* | `depart` (+ Richtung) | wie heute | `START` |
| 4 kDestination (Zwischenziel) | Hack im Coordinator | `waypoint` | `straight` | `GO_STRAIGHT` + Banner `NOTIFICATION_WAYPOINT` |
| 4/5/6 (Endziel) | finish / turn-* | `finish` | `Finish` | `END` |
| 9/16 slight, 10/15 turn, 11/14 sharp | ✓ | ✓ | ✓ | `LIGHT_*` / `QUITE_*` / `HEAVY_*` |
| 23/24 kStayRight/Left | slight-* | `keep-right/left` | slight | `KEEP_RIGHT/LEFT` |
| 22 kStayStraight | straight | `keep-straight` | straight | `KEEP_MIDDLE` |
| 17–19 kRamp* | straight / exit-* | `ramp-*` (auffahren) | slight | `ENTER_HIGHWAY_*_LANE` |
| 20/21 kExit* | exit-* | `exit-*` (abfahren) | slight | `LEAVE_HIGHWAY_*_LANE` |
| 25/37/38 kMerge* | straight / slight | `merge-*` | straight / slight | `HIGHWAY_KEEP_*` |
| 26/27 Kreisverkehr | roundabout + Ausfahrt | + `DrivingSide` | `roundabout-right/left-N` (≤ 12) | `RAB_SECT_N_RH/LH` (≤ 16) |
| 28/29 kFerry* | straight | `ferry` | straight | `FERRY` |

Die Fahrseite liefert Valhalla über `trace_attributes` (`edge.drive_on_right`). Den Aufruf gibt es schon für den
Tunnel-Index, das Attribut ließe sich also mitnehmen.

---

## 6. Interface-Skizze v2

Ursprüngliche Skizze. Wie und wie weit sie umgesetzt ist, steht unter „Stand der Umsetzung“. Abweichungen:
`Subscribe` nimmt einen Callback statt ein `IObservable` zurückzugeben (die Session räumt die Abos selbst ab),
`ReadAsync` und `ResetLinkAsync` sind entfallen, `Match`, `Capabilities`, `Events` und `SendMessageAsync` sind noch offen.

```csharp
public interface IBleConnectedDevice
{
    string DeviceId { get; }          // stabile Plattform-ID (Schlüssel für Persistenz)
    string? Name { get; }

    Task WriteAsync(Guid service, string characteristic, byte[] data, bool withResponse);
    IObservable<byte[]> Subscribe(Guid service, string characteristic); // Notify ODER Indicate
    Task<byte[]> ReadAsync(Guid service, string characteristic);

    /// Link sauber trennen und neu aufbauen lassen (hängender Handshake, W2R-Stall).
    Task ResetLinkAsync();
}

public interface IBleDevicePlugin
{
    string DisplayName { get; }
    Guid ServiceUuid { get; }                         // nur noch für den Matching-Fallback
    BleDeviceMatch? Match(BleDeviceInfo device);      // null = nicht kompatibel; sonst Marke + Konfidenz
    PluginCapabilities Capabilities { get; }

    /// Nach JEDEM Link-Up (auch Shiny-Auto-Reconnect). Abos setzen, Handshake fahren.
    /// Rückgabe: Session bereit. Fortschritt (z. B. „am Dash bestätigen“) über Events.
    Task<bool> InitializeSessionAsync(IBleConnectedDevice device, CancellationToken ct);
    void OnLinkLost();                                // Session-Zustand verwerfen

    /// Plugin → App: Tasten, Fahrzeuginfo, Telemetrie, Session-Fortschritt.
    IObservable<DeviceEvent> Events { get; }

    // ── Navigation (wie heute) ──
    Task SendNavigationStartAsync(IBleConnectedDevice d, NavigationStartInput input);
    Task SendNavigationUpdateAsync(IBleConnectedDevice d, NavigationUpdateInput input);
    Task SendNavigationFinishAsync(IBleConnectedDevice d);
    Task SendNavigationStopAsync(IBleConnectedDevice d);
    Task SendOffRouteAlertAsync(IBleConnectedDevice d);
    Task SendKeepAliveAsync(IBleConnectedDevice d);

    // ── neu ──
    Task SendMessageAsync(IBleConnectedDevice d, DashMessage message); // Sperrung, Wegpunkt, GPS weg …
    Task SendTestAsync(IBleConnectedDevice d);
}
```

Folgen für den Kern:

- `BleManagerService` ruft nach `ConnectAsync` **und** in `OnLinkRestored` `InitializeSessionAsync` auf (mit Timeout aus
  den Capabilities) und meldet erst danach `Ready`. Bei einem Linkabbruch ruft er `OnLinkLost()`.
- `SetupNotifications` und `ReadCharacteristicUuid` entfallen, das Plugin abonniert selbst.
- Der W2R-Pfad greift nur bei `withResponse: false`.
- `BleNavigationCoordinator` triggert auf `Ready` statt auf `Connected`, und der Wegpunkt-Hack wandert ins MV-Plugin.
- Das MV-Plugin bekommt in `InitializeSessionAsync` das Notify-Abo und kann später den offiziellen
  `HELLO`/`NEED`-Handshake und `GUI1 00` nachrüsten. `GUI1`-Telemetrie geht dann über `Events` an die App.

---

## 7. Was VegaBridge von der App lernen kann

Bewertet für VegaBridge (iOS-only, eigene Navigation, Dashboard-first). Alles, was Android-APIs braucht
(Notification-Listener, Accessibility-Gamepad, Media anderer Apps steuern), ist unter iOS nicht umsetzbar und fehlt hier.

### Hoher Nutzen, wenig Aufwand

1. **Symbol-Test-Seite** (`SymbolTestScreen.kt`, `TurnCalibrationScreen.kt`). Sie schickt jedes Dashboard-Icon einzeln und
   lässt den Fahrer „wird angezeigt / falsch / nichts“ markieren. Für MV ideal, um die 35 Keys auf anderen Modellen
   (Dragster, Turismo Veloce, Superveloce …) zu verifizieren, und sie passt zum Community-Aufruf im README. Generisch über
   `SupportedIcons` lösbar.
2. **GATT-Dump und Modellerkennung ins Debug-Log beim Connect** (`dumpGattTable`, Device-Info-Service `180A`). Bei
   Fehlerberichten von anderen MV-Modellen sieht man sofort, was der Dash anbietet. Bei MV zusätzlich `GUI1`
   `MODE_BIKE_ID` mitloggen, die Info kommt sowieso schon rein. Der Log-Export existiert bereits
   (`Settings.razor.cs: ExportDebugLog`).
3. **Ellipsen-Kürzung** wie im offiziellen KTM-Code (`elipse`: auf `max-3` kürzen und `...` anhängen) statt hart bei 60
   Zeichen abzuschneiden (`MvAgustaBlePlugin.cs:88`). Kleinigkeit, liest sich aber besser.
4. **PII im Log schwärzen.** Die KTM-App loggt Notification-Inhalte nur als Länge, weil Logs geteilt werden. VegaBridge
   loggt Ziel- und Off-Route-Koordinaten mit 6 Nachkommastellen, die landen beim Log-Export beim Empfänger. Eine Rundung
   auf etwa 3 Stellen oder ein Schalter beim Export wäre sinnvoll.

### Mittlerer Aufwand, gute Passung

5. **Tempolimit-Warnung.** Die KTM-App vergleicht nur mit einem festen Wert (80 km/h). VegaBridge kann es besser: Der
   `trace_attributes`-Aufruf für den Tunnel-Index ist schon da, `edge.speed_limit` als weiteres Attribut liefert das
   Tempolimit pro Kante. Anzeige im HUD und optional als Dashboard-Meldung. Die Hysterese der KTM-App (Warnung bis
   Limit − 10 km/h) und der Stale-Fix-Timeout (15 s) sind gute Details.
6. **Straßensperren aufs Dashboard.** `RoadClosureService` existiert schon. Über ein generisches `SendMessageAsync`
   (MV `MSG`, KTM Banner) bekäme der Fahrer „Sperrung in 2 km“ direkt aufs Display. Bei kurzen Displays hilft das
   Marquee-Scrolling der KTM-App (16-Zeichen-Fenster, ~3 Zeichen/s, 2 Durchläufe) als Vorlage.
7. **Stereo-Abbiegepiepen im Helm** (`TurnBeeper.kt`). Links abbiegen piept links, rechts rechts, und die Kadenz wird mit
   abnehmender Distanz schneller (Pause 1600 → 160 ms ab 400 m). Im Stand wird gedämpft, wofür VegaBridge mit
   `NavigationStatus.IsStationary` schon das Signal hat. Unter iOS mit `AVAudioEngine` (Panning) und einer passenden
   `AVAudioSession`-Kategorie machbar, unabhängig von der Marke. Erkenntnisse aus dem Feld: 750 Hz statt 1400 Hz (zu
   schrill), Kanal-Tausch-Schalter, weil Headsets L/R unterschiedlich verdrahten.
8. **Begrüßung auf dem Dash beim Connect** („Hi <Name>!“, verschwindet nach 6 s oder mit der ersten Anweisung). Das ist
   sofortiges visuelles Feedback, dass die Verbindung wirklich funktioniert, und bei MV per `MSG` möglich.

### Bewusst nicht übernehmen (vorerst)

- **Fahrtaufzeichnung mit Analyse** (Smoothness-Score, Stau-Karte, Motorerkennung über Vibration). Schön, aber gegen
  die „Dashboard-first“-Ausrichtung. GPX-Export gibt es bereits.
- **Benachrichtigungs-Spiegelung, Now-Playing, Lenker-Gamepad**: unter iOS nicht möglich (kein Notification-Listener,
  keine Accessibility-Steuerung fremder Apps).
- **ML-Icon-Erkennung**: nur nötig, weil die App Google-Maps-Benachrichtigungen ausliest. VegaBridge hat die Manöver
  strukturiert aus Valhalla.

### Falls Android je dazukommt

`Platforms/Android` existiert, ist aber nicht in den `TargetFrameworks`. Für diesen Fall ist `BccuConnectionService.kt`
eine Fundgrube an Feldwissen:

- `connectGatt(autoConnect=true)` verbindet manchmal still nie neu. Zuverlässiger ist ein gefilterter Scan nach der MAC
  und dann ein direkter Connect.
- Nach einem Disconnect `BluetoothGatt.close()` aufrufen, sonst gibt es Status-133-Fehler.
- Schaltet der Nutzer Bluetooth aus, kommt **kein** `onConnectionStateChange`. Die App muss `ACTION_STATE_CHANGED`
  selbst beobachten.
- Android erlaubt nur eine laufende GATT-Operation, daher eine Queue mit „latest value wins“-Coalescing pro
  Characteristic.
- Ab API 34 müssen Foreground-Service-Typen explizit gesetzt werden.

Shiny nimmt davon einiges ab, aber nicht alles.

---

## 8. Risiken und offene Fragen

- **Keine Testhardware.** Ohne KTM- oder Husqvarna-Fahrer mit Gen-3-Dash ist ein Plugin Blindflug. Handshake und
  Krypto sind „byte-exakt aus dekompiliertem Code“ übernommen und auf einer KTM 390 Adventure (2025) verifiziert, aber
  nicht unter iOS.
- **iOS-Bonding**: unklar, ob der Dash unter iOS zusätzlich ein OS-Pairing (verschlüsselte Characteristics) verlangt.
  Die offizielle KTM-App läuft auf iOS, grundsätzlich geht es also.
- **Freischaltung**: Laut README braucht der Dash vermutlich das Connectivity-/Tech-Pack, sonst bietet er die
  Navigationsfunktion gar nicht an.
- **Ältere KTM („MY RIDE“)** nutzen Bluetooth Classic mit JSON. Das ist unter iOS ohne MFi nicht machbar und damit kein Ziel.
- **Lizenz**: Das Referenz-Repo steht unter MIT. Eine Portierung von `BccuCrypto`/`BccuProtocol` nach C# ist erlaubt,
  wenn der Copyright-Hinweis mitkommt („Copyright (c) 2026 OpenDash contributors“).

---

## 9. Empfohlene Reihenfolge

1. ✅ **Interface-Refactoring ohne neues Plugin**: Init-Hook mit Bereit-Zustand, Session-Ende-Hook,
   Multi-Characteristic-HAL mit `withResponse`. MV-Plugin umgezogen, Verhalten unverändert, Tests grün. Der
   Event-Rückkanal folgt mit dem ersten Konsumenten.
2. ✅ **Semantische Icons erweitern** (`waypoint`, `keep-*`, `ramp-*`, `merge-*`, `ferry`) und Fahrseite. Der
   Wegpunkt-Hack ist ins Mapping und MV-Plugin gewandert.
3. **Schnelle Gewinne aus Abschnitt 7**: Symbol-Test, GATT- und Modell-Log, Ellipse, Log-PII.
4. **KTM-Plugin** erst, wenn sich ein Tester mit Gen-3-Dash findet. Bis dahin ist das Refactoring trotzdem nicht umsonst:
   Es verbessert MV (Handshake, `GUI1`-Telemetrie, Linksverkehr) und macht die Plugin-Aussage im README wieder ehrlich.
