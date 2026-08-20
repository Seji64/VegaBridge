# MV Agusta Brutale 800 — BLE Protokoll Spezifikation

> **Stand:** 2026-08-20  
> **Version:** 4.1  
> **Basis:** PacketLog `.pklg`-Capture (tshark-dekodiert) + APK Decompilation (jadx)  
> **Capture:** iPad 11,6 (iOS 26.5, Broadcom BCM4355C1) — Navigationssession  
> **App:** MV Ride v1.4.3 (Android) — dekompiliert via jadx

---

## 1. Verbindungsaufbau

| Parameter | Wert |
|-----------|------|
| Phone BT MAC | `40:E6:4B:07:24:32` |
| Bike Name | `BRUTALE_800` |
| Transport | BLE (Bluetooth Low Energy) |
| MTU | Standard BLE (23–255 Bytes, typ. 23) |

---

## 2. GATT-Profil (UUIDs)

| Handle | Eigenschaft | UUID | Verwendung |
|--------|-------------|------|------------|
| Service | — | `00003719-0000-1000-8000-00805f9b34fb` | MV Ride Service |
| `0x002A` | **Write-Characteristic** (APK: `uuidWriteCharacteristic`) | `00002345-0000-1000-8000-00805f9b34fb` | **GUI1 Dashboard-Kanal** (beidirektional, vgl. §5.11): Bike→Phone Dashboard-Indikationen; Phone→Bike `GUI1 00` (Sync) + Dashboard-Kommandos |
| `0x002D` | **Read-Characteristic** (APK: `uuidReadCharacteristic`) | `00001234-0000-1000-8000-00805f9b34fb` | Alle Navigationsbefehle (Phone→Bike Write Command im Capture) |

**Anmerkung:** Die UUIDs stammen aus der APK-Dekompilierung (`BluetoothService.java`).  
**Datenfluss:** Primär **Phone → Bike** (Navigationsbefehle). Das Bike sendet Daten über BLE-Notifications (Read-Characteristic `00001234-...`), z. B. GPS-Trip‑Downloads und NEED‑Anfragen.

> **Hinweis:** Da die Service Discovery vor Capture-Beginn stattfand, sind die UUIDs nicht im `.pklg` sichtbar. Zur Sicherheit sollten sie via nRF Connect / LightBlue direkt am Bike verifiziert werden.

**Offener Punkt (Korrektur v4.1):** Laut APK ist `00002345` die **Write**- und `00001234` die **Read**-Characteristic. Im Capture laufen aber die **Writes** der App auf `0x002D` und die **Indikationen** des Bikes auf `0x002A`. Vermutlich tragen die Characteristics mehrere Properties gleichzeitig (Write + Indicate bzw. Read + Write). Die Handle↔UUID-Zuordnung aus §2 ist daher als *vermutlich* zu lesen und per Service Discovery zu verifizieren.

**Wichtige Hinweis zu den Write-Modes:**
- **Handle `0x002A` (GUI1 Dashboard-Kanal):** Muss mit **Write mit Response** (`withResponse: true`) angesprochen werden.
- **Handle `0x002D` (alle Navigationsbefehle wie HELLO, VER, GPS, IOV, NEED, ORIG, DEST, REM, NAVI, SM, SM1, RENAVI, FINISH, G, MSG, PING):** Muss mit **Write ohne Response** (`withResponse: false`) angesprochen werden.

---

## 3. Frame-Format (auf der Leitung)

Jeder Frame ist ein UTF-8-String, der in einem ATT Write Command/Request übertragen wird.
Trennzeichen: **Record Separator** `\x1e` (RS, 0x1E).  
Terminierung: **Carriage Return** `\r` (0x0D).  
Aufbau: `\r<COMMAND>\x1e<field1>\x1e<field2>\x1e<field3>\x1e<field4>\r`

> **Konvention:** Felder werden **nicht** gequotet. Leere Felder bleiben leer (z. B. `REM\x1e\x1e1556\x1e`).

---

## 4. Befehls-Übersicht (Phone → Bike)

| Befehl | Richtung | Handle | Write-Mode | Beschreibung |
|--------|----------|--------|------------|--------------|
| `HELLO` | Phone→Bike | 0x002D | without Response | Initialisierung |
| `VER` | Phone→Bike | 0x002D | without Response | Version-Anfrage |
| `GPS` | Phone→Bike | 0x002D | without Response | GPS-Start/Stopp |
| `IOV` | Phone→Bike | 0x002D | without Response | IO-Voltage? |
| `NEED` | Phone→Bike | 0x002D | without Response | Daten anfordern |
| `ORIG` | Phone→Bike | 0x002D | without Response | Ursprung setzen (in Capture **nicht** verwendet!) |
| `DEST` | Phone→Bike | 0x002D | without Response | Zielkoordinaten (Start Navigation) |
| `REM` | Phone→Bike | 0x002D | without Response | Entfernung zum Ziel (Meter) |
| `NAVI` | Phone→Bike | 0x002D | without Response | Abbiegemanöver (Icon, Text, Straßenname) |
| `SM` | Phone→Bike | 0x002D | without Response | Status/Meter (Distanz zur Abbiegung) |
| `SM1` | Phone→Bike | 0x002D | without Response | Countdown für Abbiegung (902/901) |
| `RENAVI` | Phone→Bike | 0x002D | without Response | Rerouting-Trigger (alle Felder leer) |
| `FINISH` | Phone→Bike | 0x002D | without Response | Navigation beenden |
| `G` | Phone→Bike | 0x002D | without Response | GPS-Position vom Phone |
| `MSG` | Phone→Bike | 0x002D | without Response | Freitext-Meldung auf Display |
| `PING` | Phone→Bike | 0x002D | without Response | Keepalive — **nicht** im Android-`Command`-Enum v1.4.3; im Capture **1×** (stammt aus der iOS-App, Capture lief auf iPad). VegaBridge: 15-s-PeriodicTimer als **eigene** Keepalive-Erweiterung |
| `GUI1` | **Bidirektional** | 0x002A | Notify / Write | **Dashboard-Kanal**: Bike streamt Telemetrie (Hex-Payload `<Header><data>`); App schreibt `GUI1 00` (Sync) + Dashboard-Kommandos — Details §5.11 |

**Kritisch (Korrektur v4.1):** Der Phone schreibt GUI1 **sehr wohl** — aber nur **außerhalb der Navigation**: `GUI1 00` (Dashboard-Sync beim Connect bzw. vor Riding-Mode-Änderung) und `GUI1 <hex>` (Dashboard-Kommandos wie Quick-Shift; APK `BluetoothService.java` L.1255/L.1547). Im Nav-Capture fehlen diese Writes, weil der Capture **mid-Session** begann. Während der Navigation sendet der Phone kein GUI1 und quittiert die Indikationen lediglich (ATT-Confirmation).

---

## 5. Detaillierte Frame-Spezifikation

### 5.1 HELLO
**Korrektur v4.1 (APK-Beweis):** Die Felder sind **nicht** leer.
```
\rHELLO\x1eA\x1e<Manufacturer>\x1e<MAC>\r
```
| Feld | Inhalt | Beispiel |
|------|--------|----------|
| 1 | `A` (Konstante) | `A` |
| 2 | Geräte-Hersteller (Android: `Build.MANUFACTURER`) | `apple` / `samsung` |
| 3 | MAC-Adresse des Phones | `40:E6:4B:07:24:32` |

**Handshake (APK `startHandshaking`):** Phone sendet HELLO → wartet auf **`NEED`** vom Bike (Timeout `needTimeoutInSeconds`, bei Timeout Retry `startHandshaking(currentTime + 1)`).

### 5.2 VER
```
\rVER\x1e\x1e\x1e\x1e\r
```
- 4 leere Felder

### 5.3 DEST — Zielkoordinaten (Navigation starten)
**Capture-proven (tshark):**
```
\rDEST\x1e\x1e9.258020\x1e48.775730\x1e\r
```
| Feld | Index | Inhalt | Typ | Hinweis |
|------|-------|--------|-----|---------|
| Command | 0 | `DEST` | — | |
| Field 1 | 1 | **LEER** | string | **Immer leer!** Keine Adresse. |
| Field 2 | 2 | `9.258020` | float (6 Dezimal) | Longitude |
| Field 3 | 3 | `48.775730` | float (6 Dezimal) | Latitude |
| Field 4 | 4 | **LEER** | string | Trailing empty |

**Format:** `DEST|\x1e|<lon>\x1e|<lat>\x1e|` (3 RS = 4 Felder)

### 5.4 REM — Restliche Entfernung zum Ziel
**Capture-proven:**
```
\rREM\x1e\x1e1556\x1e\r
```
| Feld | Index | Inhalt | Typ | Hinweis |
|------|-------|--------|-----|---------|
| Command | 0 | `REM` | — | |
| Field 1 | 1 | **LEER** | string | |
| Field 2 | 2 | `1556` | int (Meter) | **Integer als String** |
| Field 3 | 3 | **LEER** | string | **Trailing empty field!** |

**Format:** `REM|\x1e|<meter>\x1e|` (3 RS = 4 Felder, trailing empty)

### 5.5 NAVI — Abbiegemanöver
**Capture-proven:**
```
\rNAVI\x1eturn-left\x1eLinks abbiegen\nRosenstraße\x1eRosenstraße\x1e\r
```
| Feld | Index | Inhalt | Typ | Hinweis |
|------|-------|--------|-----|---------|
| Command | 0 | `NAVI` | — | |
| Field 1 | 1 | `turn-left` | enum string | Semantic Icon Key |
| Field 2 | 2 | `Links abbiegen\nRosenstraße` | string | **navigationGuide** = `direction.getDescription()` |
| Field 3 | 3 | `Rosenstraße` | string | **intersectionName** = `direction.getRoadName()` (Straße **auf die** abgebogen wird) |
| Field 4 | 4 | **LEER** | string | Trailing empty |

**Format:** `NAVI|<icon>|<navigationGuide>|<intersectionName>|` (4 Felder + trailing)

**Limits (aus APK `BluetoothService.java`):**  
- `navigationGuide` ≤ **60 Zeichen** (Trunkierung!)  
- `intersectionName` ≤ **60 Zeichen** (Trunkierung!)

### 5.6 SM — Status / Distanz zur Abbiegung
**Capture-proven:**
```
\rSM\x1e0\x1e3750\x1e5\x1e\r
```
| Feld | Index | Inhalt | Typ | Hinweis |
|------|-------|--------|-----|---------|
| Command | 0 | `SM` | — | |
| Field 1 | 1 | `0` | flag | **Immer `0`** (keine Geschwindigkeit!) |
| Field 2 | 2 | `3750` | int (Meter) | Verbleibende Gesamtstrecke |
| Field 3 | 3 | `5` | int (Meter) | **Distanz zur nächsten Abbiegung** (wird angezeigt!) |
| Field 4 | 4 | **LEER** | string | Trailing empty |

**Format:** `SM|0|<remainingM>|<distanceToTurnM>|`

### 5.7 SM1 — Abbiegungs-Countdown
**Capture-proven:**
```
\rSM1\x1e902\x1e7\x1e\r
\rSM1\x1e901\x1e7\x1e\r
```
| Feld | Index | Inhalt | Typ | Hinweis |
|------|-------|--------|-----|---------|
| Command | 0 | `SM1` | — | |
| Field 1 | 1 | `902` / `901` | enum | `902` = Links-Countdown, `901` = Rechts-Countdown |
| Field 2 | 2 | `7` | int (Meter) | Countdown-Distanz |
| Field 3 | 3 | **LEER** | string | Trailing empty |

**Format:** `SM1|902|X|` (Links) / `SM1|901|X|` (Rechts)

### 5.8 RENAVI — Rerouting-Trigger
**Capture-proven:**
```
\rRENAVI\x1e\x1e\x1e\r
```
- **Alle 3 Felder leer** — nur Trigger

**Format:** `RENAVI|\x1e|\x1e|` (3 RS = 4 Felder, alle leer)

### 5.9 FINISH — Navigation beenden
**Capture-proven:**
```
\rFINISH\x1e\x1e\x1e\r
```
- 3 RS = 4 Felder, alle leer

### 5.10 PING — Keepalive
**Capture-proven (einmalig, t=1698,8 s):**
```
\rPING\x1e\x1e\x1e\r
```
- Alle Felder leer, **1× im gesamten Capture**
- **Korrektur v4.1:** PING gehört **nicht** zum Android-Protokoll v1.4.3 (fehlt im `Command`-Enum, keine PING-Referenz im App-Code). Der Capture stammt von einem iPad → der PING stammt aus der **iOS-App**.
- **VegaBridge:** Der 15-s-PeriodicTimer-Ping ist eine **eigene Keepalive-Erweiterung**, kein offizielles App-Verhalten. Im offiziellen Android-App entsteht der Keepalive-Effekt primär aus dem 1-Hz-NAVI/SM-Stream und dem HELLO/NEED-Handshake.
- **Write-Mode:** `withResponse: false` auf Handle 0x002D

### 5.11 GUI1 — Dashboard-Kanal (bidirektional)
**Korrektur v4.1:** GUI1 ist **kein** „Auth Keepalive“, und Field 1 ist **keine Session-ID**.
Field 1 ist eine **Hex-Zeichenkette** = roher Dashboard-Payload: `HeaderByte` + Datensegmente.

```
\rGUI1\x1e<hex(Header+data)>\x1e\x1e\r
```

**Header-Byte (APK `Header`-Enum):**

| Header | Byte | Bedeutung (aus `DashboardData.parse()`) |
|--------|------|------------------------------------------|
| SYNC | 0x00 | Sync-Trigger / unknown |
| ENERGY_FUEL_LEVEL | 0x20 | `fuel = b[3]` |
| BATTERY | 0x25 | `battery = (b[3] \| (b[4]<<8)) / 100` (0,01-Präzision) |
| TEMPERATURE | 0x27 | `ambient = b[2]`, `water = LE16(b[3..4])`, `oil = LE16(b[5..6])` |
| ACTIVE_RIDING_MODE | 0x84 | `mode = b[2]` (SPORT/RAIN/RACE/CUSTOM) |
| MODE_BIKE_ID | 0x85 | `modelNumber = b[2]` + Bike-Modell-String (ab `b[3]`) |
| CUSTOM_MAP | 0x86 | Riding-Mode-Flags (RPM-Limiter, Gas-Response, Torque, …) |
| QUICK_SHIFT | 0x87 | Quick-Shift (`b[1]=2`, `b[2]=0\|17`) |
| ABS | 0x8A | Assistenz-Status |
| TRACTION_CONTROL | 0x8B | Assistenz-Status |
| ENGINE_BRAKE | 0x8C | Assistenz-Status |
| FRONT_LIFT_CONTROL | 0x8E | Assistenz-Status |
| SUSPENSION_CONTROL | 0x90 | Assistenz-Status |
| OBD | 0x98 | OBD-Fehlerdaten |
| SUSPENSION_TV | 0x9B | TV-Suspension-Payload |
| GPS_TRIP_LIST … GPS_TRIP_ERASE_ONE | 0xD1–0xD6 | GPS-Trip-Download/-Manipulation |

**Capture-Decoding (Beispiele aus `mvride_nav.pklg`):**

| Hex-Payload | Decoding | Interpretation |
|-------------|----------|----------------|
| `25 00 00 BA 04 00 00 00` | BATTERY: (186 + 4·256)/100 = **12,10** | Batteriespannung (Live) |
| `25 00 00 C4 04 00 00 00` | BATTERY: (196 + 4·256)/100 = **12,20** | ← das BA/C4-„Alternieren“ ist nur der fluktuierende Live-Wert |
| `27 00 1B 19 00 00 00 00` | TEMPERATURE: **27 °C** ambient, 25 °C Water | |
| `27 00 1C 19 00 00 00 00` | **28 °C** | Temperatur steigt während der Fahrt ✔ |

**Verhalten (APK-Beweis):**
- **Bike→Phone:** Indikation auf Handle 0x002A, ~3,7 Hz (1300 Frames / 347 s im Capture). Die App parst in `DashboardData`, quittiert nur (ATT-Confirmation) — **keine** Response pro Indikation.
- **Phone→Bike:**
  - `GUI1 00` (SYNC) — **einmalig** beim Connect (`syncDashboardData()`) und vor `sendRidingMode()`; startet/refresh den Dashboard-Stream
  - `GUI1 <hex(payload)>` — Dashboard-Kommandos (z. B. Quick-Shift: `[0x87, 0x02, 0x00|0x11]`)
- **Während der Navigation: kein GUI1 vom Phone** (Capture: 0 GUI1-Writes — der Sync lag vor Capture-Beginn).

---

## 6. Navigation-Flow (aus Capture abgeleitet)

```
0. Connect:  HELLO|A|<Manufacturer>|<MAC>  →  Bike antwortet NEED (Timeout + Retry)
1. GUI1 00    (Dashboard-Sync — Bike streamt danach Dashboard-Daten ~3,7 Hz)
2. GPS        (GPS starten)
3. DEST + REM (Navigation starten — Zielkoordinaten + Restdistanz)
4. [Loop:]
   - NAVI + SM (als Pair, 1 Hz)   (Abbiegeanweisung + Status/Distanz)
   - SM1 (Countdown)              (902/901 kurz vor Abbiegung)
   - RENAVI                       (bei Abweichung — alle Felder leer)
   - [Keepalive = NAVI/SM-Stream; Android v1.4.3 sendet **kein** PING]
5. FINISH     (Navigation beenden)
6. PING (alle 15s)   (nur iOS-App / VegaBridge-eigene Erweiterung — nicht Teil des Android-Protokolls v1.4.3)
```

**Kein expliziter "Navigation Mode Activation" Befehl** — Navigation startet implizit mit DEST/NAVI/SM Frames.

---

## 7. Off-Route Erkennung

| Parameter | Wert | Quelle |
|-----------|------|--------|
| Threshold | 25 Meter | Konfiguration |
| Accuracy-Multiplier | 1.5 | `accuracy * 1.5` |
| Auslösung | `RENAVI` (alle leer) | Capture-proven |

---

## 8. Valhalla Maneuver → Semantic Icon Mapping

| Valhalla Type | Semantic Key | Anzeige |
|---------------|--------------|---------|
| `TurnLeft` | `turn-left` | Links abbiegen |
| `TurnRight` | `turn-right` | Rechts abbiegen |
| `TurnSlightLeft` | `turn-slight-left` | Leicht links |
| `TurnSlightRight` | `turn-slight-right` | Leicht rechts |
| `TurnSharpLeft` | `turn-sharp-left` | Scharf links |
| `TurnSharpRight` | `turn-sharp-right` | Scharf rechts |
| `Roundabout` | `roundabout` | Kreisverkehr |
| `Merge` | `merge` | Einfädeln |
| `OnRamp` | `on-ramp` | Auffahrt |
| `OffRamp` | `off-ramp` | Abfahrt |
| `ForkLeft` | `fork-left` | Links halten |
| `ForkRight` | `fork-right` | Rechts halten |
| `UTurnLeft` | `u-turn-left` | Wenden links |
| `UTurnRight` | `u-turn-right` | Wenden rechts |
| `Continue` | `straight` | Geradeaus |
| `Start` / `StartAtEndOfStreet` | `depart` | Start |
| `End` / `DestinationReached` | `arrive` | Ziel erreicht |
| *unbekannt* | `straight` | Fallback |

**Nur bei "straight" (Continue/Start) wird das nächste Manöver vorgeholt (Look-Ahead).**  
Fenster: **20 Meter** (`RouteLookaheadWindow`).

---

## 9. Implementierungs-Status im Codebase

| Komponente | Status | Details |
|------------|--------|---------|
| `MvAgustaBlePlugin.cs` | ✅ Fertig | DEST/REM/RENAVI/PING korrigiert, GUI1 Write entfernt |
| `Commands.cs` | ✅ Fertig | `PING` Konstante hinzugefügt |
| `BleNavigationCoordinator.cs` | ✅ Fertig | Frame-Aufbau korrigiert, Event-Handling |
| `NavigationService.cs` | ✅ Fertig | Koordiniert NavigationStart/Update/Finish |
| `NavigationIconMapper.cs` | ✅ Fertig | Valhalla→Semantic Mapping korrekt |
| `Map.razor` / `.cs` | ✅ Fertig | UI + GPS + Navigation Integration |
| `Settings.razor` | ✅ Fertig | Nur "Test MSG" Button, BLE Log Export |
| `BleCommandLogger.cs` | ✅ Fertig | File-based Export (FileSaver) |

---

## 10. Testsequenzen (für On-Bike-Validierung)

### 10.1 Minimaler Test (nur MSG)
```
MSG "Test 1"
```
→ **Sofort sichtbare Meldung auf Display**

### 10.2 Navigation Test (Rapid-Fire, keine Delays!)
```
HELLO
VER
GPS
DEST|""|<lon>|<lat>|"
REM|""|1000|""
NAVI|turn-left|Links abbiegen\nHauptstraße|Hauptstraße|
SM|0|1000|50|
SM1|902|10|
SM|0|950|20|
SM1|902|5|
NAVI|turn-right|Rechts abbiegen\nNebenstraße|Nebenstraße|
SM|0|900|30|
SM1|901|10|
SM|0|850|15|
RENAVI|||      (falls off-route)
FINISH|||"
```
**Wichtig:** **Keine `Task.Delay`** zwischen Frames! Rapide Senden, BLE-Stack-Timeouts nutzen.

---

## 11. Capture-Analyse Werkzeuge

```bash
# .pklg → Text (tshark)
tshark -r mvride_nav.pklg -Y "btatt.opcode == 0x52" -T fields -e btatt.value > mvride_nav_raw.txt

# Filter: Phone→Bike (Handle 0x002D)
tshark -r mvride_nav.pklg -Y "btatt.handle == 0x002d && btatt.opcode == 0x52" -T fields -e frame.time_relative -e btatt.value

# Filter: Bike→Phone GUI1 (Handle 0x002A, Notification)
tshark -r mvride_nav.pklg -Y "btatt.handle == 0x002a && btatt.opcode == 0x1b" -T fields -e frame.time_relative -e btatt.value
```

---

## 12. Bekannte Fallstricke

| Problem | Lösung |
|---------|--------|
| DEST mit Adresse in Feld 1 | **Feld 1 leer lassen!** Nur Lon/Lat (6 Dezimal) |
| REM ohne trailing empty | **3 RS senden** (`REM\x1e\x1e<m>\x1e`) |
| RENAVI mit Text | **Alle Felder leer!** |
| GUI1 als „Session-ID“ deuten | **Falsch!** GUI1 = Dashboard-Kanal, Field 1 = Hex(`<Header><data>`), vgl. §5.11 |
| GUI1 während Navigation senden | Nicht nötig; `GUI1 00` (Sync) nur beim Connect / vor Riding-Mode-Änderung |
| PING als offizielles App-Verhalten annehmen | Nicht in Android v1.4.3; 15-s-PING = VegaBridge-eigene Keepalive-Erweiterung (iOS-App sendet PING) |
| Feste Delays in Tests | **Rapid-Fire, BLE-Timeouts nutzen** |
| NAVI Strings > 60 Zeichen | **Trunkieren** (APK macht das auch) |

---

## 13. Weiteres Vorgehen mit LightBlue (iOS)

Detaillierte Anleitung für BLE-Analyse mit iPhone + LightBlue App:

1. **LightBlue installieren** (App Store)
2. **BRUTALE_800** verbinden
3. **Service `00003719-...`** erkunden
4. **Characteristic `00002345-...` (0x002A)** → Notify aktivieren → GUI1 Dashboard-Payloads beobachten (Spannung/Temperatur, vgl. §5.11)
5. **Characteristic für 0x002D** (Write Command) → Test-Frames senden (HELLO, MSG, etc.)
6. **MV Ride App parallel starten** → Navigation starten → Traffic in LightBlue "Log" beobachten
7. **Vergleichen** mit `mvride_nav.txt` Ground Truth

---

## 📝 Änderungsprotokoll

| Datum | Version | Änderungen |
|-------|---------|------------|
| 2026-07-16 | v1.0 | Erstfassung basierend auf `mvride_nav.pklg`-Capture |
| 2026-07-16 | v2.0 | APK-Dekompilierung (jadx) – alle 16 Nachrichtentypen, UUIDs, Turn-Enum, Pairing-Mechanismus |
| 2026-08-27 | v3.0 | Ergänzung Abschnitt 13: Weiteres Vorgehen mit LightBlue (iOS) – detaillierte Test‑ und Beobachtungsanleitung für BLE‑Analyse mit iPhone. |
| 2026-08-27 | v3.1 | Hinzufügung der Write-Mode-Informationen für die Characteristic UUIDs und Implementierung des GUI1-Heartbeat-Mechanismus im MvAgustaBlePlugin. |
| 2026-08-08 | v3.2 | Erweiterung der Testsequenzen um Mehrphasen-Navigation: Linksabbieger → 10s → Rechtsabbieger → 10s → FINISH. |
| 2026-08-09 | v3.3 | **SM/SM1 final aus On-Bike-Tests**: 1. SM-Feld ist Flag `0` (keine Geschwindigkeit), 3. Feld = Distanz zur Abbiegung (wird angezeigt). SM1 `902` = Links-Countdown, `901` = Rechts-Countdown. Spec §8 und §9 aktualisiert. |
| 2026-08-09 | v3.4 | **NAVI Frame korrigiert**: 4 Felder (NAVI|icon|navigationGuide|intersectionName) mit 60-Zeichen-Limit. `navigationGuide` = `direction.getDescription()`, `intersectionName` = `direction.getRoadName()` (Straße **auf die** abgebogen wird). Plugin & Coordinator aktualisiert. Testsequenzen korrigiert. |
| 2026-08-09 | v4.0 | **Frame-Formate aus pklg-Analyse (tshark) korrigiert**: DEST = `DEST|\x1e|lon\x1e|lat\x1e|` (Feld 1 leer!), REM = `REM|\x1e|<meter>\x1e|` (3 RS = 4 Felder, trailing empty), RENAVI = alle Felder leer, FINISH = 3 RS (4 Felder), PING = `PING|\x1e|\x1e|\x1e|` (einmalig im Capture). **Phone sendet NIEMALS GUI1** — alle GUI1 sind Bike→Phone Notifications. GUI1-Heartbeat entfernt, PING-Keepalive implementiert. MvAgustaBlePlugin: DEST/REM/RENAVI/FINISH/PING korrigiert, GUI1 Write entfernt. |
| 2026-08-20 | v4.1 | **GUI1-Mystery gelöst (tshark × APK-Kreuzanalyse, §2/§4/§5.1/§5.10/§5.11/§6/§12):** GUI1 ist **kein** „Auth Keepalive“ mit Session-ID, sondern ein **bidirektionaler Dashboard-Kanal** (Header-Enum + Hex-Payload; Capture-Beispiele = Batteriespannung 12,10/12,20 V + Temperaturen 27/28 °C). Die App schreibt GUI1 sehr wohl: `GUI1 00` (Sync beim Connect / vor Riding-Mode) + Dashboard-Kommandos (z. B. Quick-Shift) — nur nicht während der Navigation (Sync lag vor Capture-Beginn). **PING** gehört nicht zum Android-Protokoll v1.4.3 (fehlt im `Command`-Enum); der 1×-PING im Capture stammt aus der iOS-App (iPad-Capture), der 15-s-Ping ist VegaBridge-eigene Erweiterung. HELLO-Felder korrigiert (`HELLO\|A\|<Manufacturer>\|<MAC>` + NEED-Handshake-Flow). UUID-Table mit APK-Korrektur + offener Handle↔UUID-Punkt. |