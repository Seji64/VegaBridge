# MV Agusta Brutale 800 — BLE Protokoll Spezifikation

> **Stand:** 2026-07-16  
> **Basis:** PacketLog `.pklg`-Capture + APK Decompilation (jadx)  
> **Capture:** iPad 11,6 (iOS 26.5, Broadcom BCM_4355C1) — Navigationssession  
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
| `0x002A` | Write mit Response | `00002345-0000-1000-8000-00805f9b34fb` | GUID/Auth Keepalive |
| `0x002D` | Write Command | *(unbekannt, im Capture nicht sichtbar)* | Navigationsdaten |
| ? | Notify/Read | `00001234-0000-1000-8000-00805f9b34fb` | Bike → Phone (GPS-Download, NEED) |

**Anmerkung:** Die UUIDs stammen aus der APK-Dekompilierung (`BluetoothService.java`).
**Datenfluss:** Primär **Phone → Bike** (Navigationsbefehle). Das Bike sendet Daten über BLE-Notifications (auf dem Read-Characteristic `00001234-...`), z. B. GPS-Trip‑Downloads und NEED‑Anfragen.

> **Hinweis:** Da die Service Discovery vor Capture-Beginn stattfand, sind die UUIDs nicht im `.pklg` sichtbar.
> Zur Sicherheit sollten sie via nRF Connect / LightBlue direkt am Bike verifiziert werden.

---

## 3. Nachrichtenformat (Application Layer)

### 3.1 Allgemeines Frame-Format

```
<0x0D><TYPE><0x1E><Feld1><0x1E><Feld2><0x1E><Feld3><0x0D>
```

| Byte | Bedeutung | ASCII |
|------|-----------|-------|
| `0x0D` | Start- und End-Marker | CR (Carriage Return, `\r`) |
| `0x1E` | Feldtrenner | RS (Record Separator) |

Alle Felder sind **UTF-8 Text** (ermöglicht Umlaute wie `ß`, `ä`, `ö`, `ü`).

### 3.2 CommandDataPacket (aus APK)

Die APK-Klasse `CommandDataPacket` erzeugt den Frame via:

```kotlin
fun toBluetoothData(): ByteArray {
    val sb = StringBuilder("\r")            // Start-CR
    sb.append(fields[0])                     // Command (z.B. "NAVI")
    for (i in 1 until fields.size) {
        sb.append("\u001E").append(fields[i]) // RS + Feld
    }
    sb.append("\r")                          // End-CR
    return sb.toString().toByteArray(Charsets.UTF_8)
}
```

Ein `CommandDataPacket` hat maximal **4 Felder** (Feld 0 = Command, Felder 1–3 = Daten).

---

## 4. Nachrichtentypen (16 Befehle)

| # | Befehl | APK-Enum | Beschreibung |
|---|--------|----------|-------------|
| 1 | **HELLO** | — | Verbindungshandshake |
| 2 | **GUI1** | GUI | Session Keepalive (GUID-Paar) |
| 3 | **VER** | — | Versionscheck |
| 4 | **GPS** | — | Vollständige GPS-Daten |
| 5 | **IOV** | WIFIHOTSPOT | WLAN-Konfiguration |
| 6 | **NEED** | — | Anfrage vom Bike |
| 7 | **ORIG** | START_ADDRESS | Routen-Startpunkt |
| 8 | **DEST** | DESTINATION_ADDRESS | Routenziel |
| 9 | **REM** | DESTINATION_DISTANCE | Verbleibende Distanz |
| 10 | **NAVI** | TURN_BY_TURN_DIRECTION | Abbiege-Hinweis |
| 11 | **SM** | TURN_BY_TURN_SPEED_REMAIN_DISTANCE | Geschwindigkeit + Distanzen |
| 12 | **SM1** | TURN_BY_TURN_ESTIMATED_TIME_ARRIVAL | ETA (alle 30s) |
| 13 | **RENAVI** | RE_ROUTE | Routenneuberechnung |
| 14 | **FINISH** | FINISH_ROUTE | Navigation beendet |
| 15 | **G** | GPS_PARTIAL_DATA | Teil-GPS-Updates |
| 16 | **MSG** | — | Allgemeine Meldungen |

---

### 4.1 `HELLO` — Verbindungshandshake

| Handle | Typ |
|--------|-----|
| `0x002D` | Write Command |

```
⏎HELLO⏝A⏝<hersteller>⏝<MAC>⏎
```

| Feld | Beispiel | Beschreibung |
|------|----------|-------------|
| `A` | `A` | Feste Kennung |
| `hersteller` | `Apple` | `Build.MANUFACTURER` des Android-Geräts |
| `MAC` | `40:E6:4B:07:24:32` | MAC-Adresse des sendenden Geräts |

**Wann:** Zu Beginn jeder neuen BLE-Verbindung, noch vor allen Navigationsdaten.

**APK-Code:**
```kotlin
CommandDataPacket(arrayOf(
    "HELLO",
    "A",
    Build.MANUFACTURER,
    bluetoothDevice.address
))
```

---

### 4.2 `GUI1` — Session Keepalive

| Handle | Typ |
|--------|-----|
| `0x002A` | Write mit Response |

```
⏎GUI1⏝<session_id_hex>⏎
```

| Feld | Beispiel | Beschreibung |
|------|----------|-------------|
| `session_id` | `250000BA04000000` | Hex-String als Session-ID. Alle 1–3 s aktualisiert. |

**Paare:** Die GUIDs werden immer **paarweise** gesendet, z.B. `BA04` + `C404` oder `B190` + `B1A0`.

| GUID-A | GUID-B | Zeitraum |
|--------|--------|----------|
| `250000BA04000000` | `250000C404000000` | ~20 s |
| `270001B190000000` | `270001B1A0000000` | danach |

---

### 4.3 `VER` — Versionscheck

| Handle | Typ |
|--------|-----|
| `0x002D` | Write Command |

```
⏎VER⏝<versions_string>⏎
```

Versionsstring der App, z.B. `1.4.3`.

---

### 4.4 `GPS` — Vollständige GPS-Daten

| Handle | Typ |
|--------|-----|
| `0x002D` | Write Command |

```
⏎GPS⏝<lat>⏝<lon>⏝<speed_ms>⏝<heading>⏎
```

| Feld | Beschreibung |
|------|-------------|
| `lat` | Breitengrad (dezimal, z.B. `48.775730`) |
| `lon` | Längengrad (dezimal, z.B. `9.258020`) |
| `speed_ms` | Geschwindigkeit in m/s |
| `heading` | Kurs in Grad (0–360) |

---

### 4.5 `IOV` — WLAN-Konfiguration

| Handle | Typ |
|--------|-----|
| `0x002D` | Write Command |

```
⏎IOV⏝WIFI_HOTSPOT⏝<ssid>⏝<passwort>⏎
```

Sendet WLAN-Zugangsdaten ans Bike (z.B. für OTA-Updates).

---

### 4.6 `NEED` — Anfrage vom Bike

| Handle | Typ |
|--------|-----|
| `0x002D` | Write Command |

```
⏎NEED⏝⏝⏝⏝⏎
```

Wird vom **Bike** gesendet, um bestimmte Daten anzufordern. Der genaue Kontext ist noch unklar (wird beim Pairing/Handshake verwendet).

---

### 4.7 `ORIG` — Routen-Start

| Handle | Typ |
|--------|-----|
| `0x002D` | Write Command |

```
⏎ORIG⏝<adresse>⏝<lat>⏝<lon>⏎
```

| Feld | Beschreibung |
|------|-------------|
| `adresse` | Startadresse (Text) |
| `lat` | Breitengrad (dezimal) |
| `lon` | Längengrad (dezimal) |

---

### 4.8 `DEST` — Routenziel

| Handle | Typ |
|--------|-----|
| `0x002D` | Write Command |

```
⏎DEST⏝<adresse>⏝<lat>⏝<lon>⏎
```

| Feld | Beschreibung |
|------|-------------|
| `adresse` | Zieladresse (Text) |
| `lat` | Breitengrad (dezimal) |
| `lon` | Längengrad (dezimal) |

Wird jedes Mal nach einem `RENAVI` erneut gesendet.

---

### 4.9 `REM` — Restdistanz

| Handle | Typ |
|--------|-----|
| `0x002D` | Write Command |

```
⏎REM⏝⏝<meter>⏎
```

| Feld | Beispiel | Beschreibung |
|------|----------|-------------|
| `meter` | `3424` | Verbleibende Gesamtdistanz zum Ziel in Metern. |

---

### 4.10 `NAVI` — Abbiegehinweis

| Handle | Typ |
|--------|-----|
| `0x002D` | Write Command |

```
⏎NAVI⏝<icon>⏝<anweisung>⏝<straße>⏎
```

| Feld | Beschreibung |
|------|-------------|
| `icon` | Abbiegetyp (siehe §5 Turn-Typen) |
| `anweisung` | Text-Anweisung, max. 60 Zeichen |
| `straße` | Straßenname, max. 60 Zeichen |

**APK-Code:**
```kotlin
CommandDataPacket(arrayOf(
    "NAVI",
    icon.value,                   // z.B. "turn-left"
    navigationGuide.take(60),     // max 60 Chars
    intersectionName.take(60)     // max 60 Chars
))
```

**Beispiele aus dem Capture:**

| Icon | Anweisung | Straße |
|------|-----------|--------|
| `turn-left` | `Links abbiegen` | `Rosenstraße` |
| `turn-left` | `Links abbiegen` | `Rieslingstraße` |
| `turn-right` | `Rechts abbiegen` | `Obertürkheimer Straße` |
| `roundabout-right-1` | `Nehmen Sie die 1. Ausfahrt` | `Imweg` |

---

### 4.11 `SM` — Geschwindigkeit & Distanzen

| Handle | Typ |
|--------|-----|
| `0x002D` | Write Command |

```
⏎SM⏝<speed_kmh>⏝<dest_rest_m>⏝<turn_rest_m>⏎
```

| Feld | APK-Quelle | Beschreibung |
|------|-----------|-------------|
| `speed_kmh` | `String.valueOf(speedKmh)` | Aktuelle Geschwindigkeit in km/h (0 wenn nicht verfügbar) |
| `dest_rest_m` | `String.valueOf(destinationRemainDistanceMeters)` | Restdistanz **zum Ziel** in Metern |
| `turn_rest_m` | `String.valueOf(turnRemainDistanceMeters)` | Distanz **zur nächsten Abbiegung** in Metern |

**Das 3. Feld war in der ersten Analyse als `???` markiert – dank der APK ist es jetzt eindeutig:**
Es ist `turnRemainDistanceMeters` – die Entfernung bis zur nächsten Abbiegung/Navigationsanweisung.

**APK-Code (aus `sendTurnByTurnIndication$lambda$53`):**
```kotlin
CommandDataPacket(arrayOf(
    "SM",
    speedKmh.toString(),            // = Feld 1
    destinationRemainDistanceMeters.toString(),  // = Feld 2
    turnRemainDistanceMeters.toString()          // = Feld 3
))
```

Der SM-Befehl wird **direkt nach NAVI** gesendet (als Callback auf den NAVI-Send):
```kotlin
// 1. NAVI senden
this$0.send(naviPacket, "send turn by turn indication",
    // 2. callback → SM senden
    callback(emitter, this$0, smPacket))
```

**Beispiel-Sequenz aus dem Capture – Schritt für Schritt:**

```
⏎NAVI⏝turn-left⏝Links abbiegen⏝Rosenstraße⏎  ← Neue Anweisung
⏎SM⏝0⏝3750⏝5⏎        ← speed=0, Ziel=3750m, Turn=5m (wir sind fast an der Abbiegung)

⏎NAVI⏝turn-left⏝Links abbiegen⏝Rieslingstraße⏎ ← Nächste Anweisung (alten abbiegung vorbei)
⏎SM⏝0⏝3700⏝230⏎      ← speed=0, Ziel=3700m, Turn=230m (neue Abbiegung ist 230m entfernt)

⏎SM⏝0⏝3650⏝193⏎      ← speed=0, Ziel=3650m, Turn=193m (wir nähern uns)
⏎SM⏝0⏝3500⏝20⏎       ← speed=0, Ziel=3500m, Turn=20m  (kurz vor der Abbiegung)
⏎SM⏝0⏝3500⏝0⏎        ← speed=0, Ziel=3500m, Turn=0m   (an der Abbiegung)
```

Die **springenden Werte** (5 → 230) sind kein Fehler – sie erklären sich durch den Wechsel des aktiven Routenpunkts: 
- `5m` war die Restdistanz zur **alten** Abbiegung (Rosenstraße)
- Nach Passieren der alten Abbiegung springt die Anzeige auf die **nächste** Anweisung mit `230m` (Rieslingstraße)

> **Hinweis zum 1. Feld:** Im Capture steht dort immer `0`. Die APK verwendet `String.valueOf(speedKmh)`. 
> Da die Geschwindigkeit nicht im Capture sichtbar ist, wurde sie vermutlich nicht vom Phone geliefert (kein GPS-Speed?).

**SM wird alle 1-2 Sekunden aktualisiert**, während die Navigation aktiv ist.

---

### 4.12 `SM1` — Navigationsstatus / ETA

| Handle | Typ |
|--------|-----|
| `0x002D` | Write Command |

```
⏎SM1⏝<route_info>⏝<step_or_minutes>⏎
```

| Feld | APK-Quelle | Beschreibung |
|------|-----------|-------------|
| `route_info` | `String.valueOf(destinationArriveTimeInMinutes)` | Laut APK: Minuten bis Ankunft. Im Capture: Routen-/Schritt-ID |
| `step_or_minutes` | `String.valueOf(destinationRemainTimeInMinutes)` | Laut APK: verbleibende Minuten. Im Capture: Schritt-Nummer (dekrementierend) |

**APK-Enum:** `TURN_BY_TURN_ESTIMATED_TIME_ARRIVAL` = "SM1"

**APK-Code (aus `sendNavigationStatusEveryThirtySeconds$lambda$54`):**
```kotlin
this$0.send(new CommandDataPacket(new String[]{
    Command.TURN_BY_TURN_ESTIMATED_TIME_ARRIVAL.getValue(),  // "SM1"
    String.valueOf(i4),   // destinationArriveTimeInMinutes
    String.valueOf(i10)   // destinationRemainTimeInMinutes
}), "send navigation status each 30s", ...);
```

**Hinweis:** Im vorliegenden Capture sehen die SM1-Werte allerdings nicht nach Minuten aus, sondern nach Routen-Schritt-Informationen:

```
⏎SM1⏝902⏝7⏎          ← Route 902, Schritt 7
⏎SM1⏝901⏝6⏎          ← Route 901, Schritt 6 (nach RENAVI neue Route)
⏎SM1⏝901⏝5⏎          ← Schritt 5
⏎SM1⏝902⏝4⏎          ← Schritt 4, wieder Route 902
⏎SM1⏝902⏝3⏎          ← Schritt 3
⏎SM1⏝902⏝2⏎          ← Schritt 2
```

Es scheint **zwei unterschiedliche Nutzungen von SM1 zu geben**:
1. **Navigationsfortschritt** (Route-ID + Step) – wird bei jedem Wegpunkt-Update gesendet
2. **ETA-Update** (Ankunftsminuten + Restminuten) – wird nur bei langer aktiver Navigation alle 30s gesendet

> **Offen:** Ob die APK-Methode `sendNavigationStatusEveryThirtySeconds` im Capture einfach noch nicht gefeuert hatte oder ob die Werte im Capture aus einem anderen Codepfad stammen, lässt sich nur mit einem längeren Capture klären.

---

### 4.13 `RENAVI` — Routenneuberechnung

| Handle | Typ |
|--------|-----|
| `0x002D` | Write Command |

```
⏎RENAVI⏝⏝⏝⏎
```

Wird bei Neuberechnung gesendet. Das Bike zeigt daraufhin die neue Route an.

**Typische Sequenz nach RENAVI:**
```
RENAVI → DEST → REM → NAVI → SM
```

---

### 4.14 `FINISH` — Navigation beendet

| Handle | Typ |
|--------|-----|
| `0x002D` | Write Command |

```
⏎FINISH⏝⏝⏝⏎
```

---

### 4.15 `G` — Teil-GPS

| Handle | Typ |
|--------|-----|
| `0x002D` | Write Command |

```
⏎G⏝<lat>⏝<lon>⏝<timestamp>⏎
```

Kompakte GPS-Updates zwischen vollen `GPS`-Paketen.

---

### 4.16 `MSG` — Allgemeine Meldung

| Handle | Typ |
|--------|-----|
| `0x002D` | Write Command |

```
<0x0D>MSG<0x1E><applicationId><0x1E><message><0x1E><title><0x0D>
```

- **applicationId**: Kennung der Quelle (z. B. "whatsapp", "telegram", "messenger", "sms", "missedcall")
- **message**: Der Text der Benachrichtigung (z. B. die Nachricht selbst)
- **title**: Der Titel der Benachrichtigung (z. B. "WhatsApp" oder der Absender)

Die App überträgt die vollständigen Strings ohne Kürzung. Die Anzeige auf dem Bike kann je nach Firmware‑Version unterschiedlich sein: ältere Firmware zeigte die ersten ca. 60 Zeichen der Nachricht, neuere Versionen zeigen möglicherweise nur ein Benachrichtigungssymbol.

---

## 5. Turn-Typen (Icon-Enum)

Vollständige Liste der `TurnByTurnIndication`-Enum-Werte aus der APK:

| Icon (Englisch) | Enum-Wert | Deutsche Beschreibung |
|-----------------|-----------|-----------------------|
| `turn-left` | TURN_LEFT | Links abbiegen |
| `turn-right` | TURN_RIGHT | Rechts abbiegen |
| `uturn-left` | U_TURN_LEFT | Wenden (links) |
| `uturn-right` | U_TURN_RIGHT | Wenden (rechts) |
| `turn-slight-left` | TURN_SLIGHT_LEFT | Leicht links |
| `turn-slight-right` | TURN_SLIGHT_RIGHT | Leicht rechts |
| `roundabout-left-1` | ROUNDABOUT_LEFT1 | Kreisverkehr, 1. Ausfahrt (linksherum) |
| `roundabout-left-2` | ROUNDABOUT_LEFT2 | Kreisverkehr, 2. Ausfahrt (linksherum) |
| `roundabout-left-3` | ROUNDABOUT_LEFT3 | Kreisverkehr, 3. Ausfahrt (linksherum) |
| `roundabout-left-4` | ROUNDABOUT_LEFT4 | Kreisverkehr, 4. Ausfahrt (linksherum) |
| `roundabout-left-5` | ROUNDABOUT_LEFT5 | Kreisverkehr, 5. Ausfahrt (linksherum) |
| `roundabout-left-6` | ROUNDABOUT_LEFT6 | Kreisverkehr, 6. Ausfahrt (linksherum) |
| `roundabout-left-7` | ROUNDABOUT_LEFT7 | Kreisverkehr, 7. Ausfahrt (linksherum) |
| `roundabout-left-8` | ROUNDABOUT_LEFT8 | Kreisverkehr, 8. Ausfahrt (linksherum) |
| `roundabout-left-9` | ROUNDABOUT_LEFT9 | Kreisverkehr, 9. Ausfahrt (linksherum) |
| `roundabout-left-10` | ROUNDABOUT_LEFT10 | Kreisverkehr, 10. Ausfahrt (linksherum) |
| `roundabout-left-11` | ROUNDABOUT_LEFT11 | Kreisverkehr, 11. Ausfahrt (linksherum) |
| `roundabout-left-12` | ROUNDABOUT_LEFT12 | Kreisverkehr, 12. Ausfahrt (linksherum) |
| `roundabout-right-1` | ROUNDABOUT_RIGHT1 | Kreisverkehr, 1. Ausfahrt (rechtsherum) |
| `roundabout-right-2` | ROUNDABOUT_RIGHT2 | Kreisverkehr, 2. Ausfahrt (rechtsherum) |
| `roundabout-right-3` | ROUNDABOUT_RIGHT3 | Kreisverkehr, 3. Ausfahrt (rechtsherum) |
| `roundabout-right-4` | ROUNDABOUT_RIGHT4 | Kreisverkehr, 4. Ausfahrt (rechtsherum) |
| `roundabout-right-5` | ROUNDABOUT_RIGHT5 | Kreisverkehr, 5. Ausfahrt (rechtsherum) |
| `roundabout-right-6` | ROUNDABOUT_RIGHT6 | Kreisverkehr, 6. Ausfahrt (rechtsherum) |
| `roundabout-right-7` | ROUNDABOUT_RIGHT7 | Kreisverkehr, 7. Ausfahrt (rechtsherum) |
| `roundabout-right-8` | ROUNDABOUT_RIGHT8 | Kreisverkehr, 8. Ausfahrt (rechtsherum) |
| `roundabout-right-9` | ROUNDABOUT_RIGHT9 | Kreisverkehr, 9. Ausfahrt (rechtsherum) |
| `roundabout-right-10` | ROUNDABOUT_RIGHT10 | Kreisverkehr, 10. Ausfahrt (rechtsherum) |
| `roundabout-right-11` | ROUNDABOUT_RIGHT11 | Kreisverkehr, 11. Ausfahrt (rechtsherum) |
| `roundabout-right-12` | ROUNDABOUT_RIGHT12 | Kreisverkehr, 12. Ausfahrt (rechtsherum) |
| `bridge` | BRIDGE | Brücke |
| `underpass` | UNDERPASS | Tunnel |
| `straight` | STRAIGHT | Geradeaus |
| `Finish` | FINISH | Ziel erreicht |
| `af` | AF | Unbestimmt (Feldweg?) |

---

## 6. Nachrichtenabläufe

### 6.1 Normalbetrieb (Navigation)

```
HELLO⏝A⏝Apple⏝40:E6:4B:07:24:32⏎        ← Verbindungsaufbau
GUI1⏝250000BA04000000⏎                     ← Keepalive-Paar (alle 1–3 s)
GUI1⏝250000C404000000⏎
...
NAVI⏝turn-left⏝Links abbiegen⏝Rosenstraße⏎ ← Abbiegehinweis
SM⏝0⏝3750⏝5⏎                              ← Geschwindigkeit & Distanzen
GUI1⏝250000BA04000000⏎                     ← Keepalive
GUI1⏝250000C404000000⏎
...
SM1⏝15⏝12⏎                                 ← ETA (alle 30 s)
...
NAVI⏝turn-right⏝Rechts abbiegen⏝Obertürkheimer Str.⏎
SM⏝0⏝3400⏝60⏎
...
FINISH⏝⏝⏝⏎                                 ← Navigation beendet
```

### 6.2 Routenneuberechnung

```
PING⏝⏝⏝⏎                                  ← Keepalive (alle 3–5 s)
RENAVI⏝⏝⏝⏎                                 ← App berechnet Route neu
DEST⏝⏝9.258020⏝48.775730⏎                  ← Ziel erneut senden
REM⏝⏝3424⏎                                 ← Aktualisierte Restdistanz
NAVI⏝turn-left⏝Links abbiegen⏝...⏎        ← Neue Anweisung
SM⏝0⏝3400⏝60⏎
```

---

## 7. HEX-Referenz

| Typ | Hex-Payload (ohne ATT-Header) |
|-----|-------------------------------|
| GUI1 (BA04) | `0D 47 55 49 31 1E 32 35 30 30 30 30 42 41 30 34 30 30 30 30 30 30 0D` |
| GUI1 (C404) | `0D 47 55 49 31 1E 32 35 30 30 30 30 43 34 30 34 30 30 30 30 30 30 0D` |
| NAVI (turn-left) | `0D 4E 41 56 49 1E 74 75 72 6E 2D 6C 65 66 74 1E 4C 69 6E 6B 73 20 61 62 62 69 65 67 65 6E 0A 52 6F 73 65 6E 73 74 72 61 C3 9F 65 1E 52 6F 73 65 6E 73 74 72 61 C3 9F 65 0D` |
| SM (3750m) | `0D 53 4D 1E 30 1E 33 37 35 30 1E 35 0D` |
| SM1 (902/7) | `0D 53 4D 31 1E 39 30 32 1E 37 1E 0D` |
| PING | `0D 50 49 4E 47 1E 1E 1E 0D` |
| DEST | `0D 44 45 53 54 1E 1E 39 2E 32 35 38 30 32 30 1E 34 38 2E 37 37 35 37 33 30 0D` |
| REM (3424m) | `0D 52 45 4D 1E 1E 33 34 32 34 1E 0D` |
| RENAVI | `0D 52 45 4E 41 56 49 1E 1E 1E 0D` |
| FINISH | `0D 46 49 4E 49 53 48 1E 1E 1E 0D` |

---

## 8. Offene Fragen

### ❓ Unbekannte Felder

| Frage | Beschreibung |
|-------|-------------|
| **SM – 1. Feld (speed_kmh)** | Im Capture immer `0`. Fehlte GPS-Speed? Geschwindigkeit vom Bike-Display kommt anders? |
| **SM1 – route_id vs. ETA** | Capture zeigt `901/902+Step`, APK sagt `arrivalMinutes+remainMinutes`. Werden zwei verschiedene SM1-Varianten benutzt? |
| **GUID-Wechsel** | Wann wechselt das Format von `BA04`/`C404` zu `B190`/`B1A0`? |

### 📋 Fehlende Captures

- **Pairing/Bonding** – Wie läuft der erstmalige Kopplungsprozess ab? (Siehe APK-Analyse §10)
- **Telefonie** – Welche Nachrichten bei eingehenden Anrufen?
- **Musik** – Werden Titel/Metadaten übertragen?
- **Fahrzeugdaten** – Sendet das Bike aktiv Daten (Geschwindigkeit, Drehzahl, Modi)?
- **Dashboard** – Welche GUI1-Bytefelder steuern was?

---

## 9. UUID-Referenz

| Komponente | UUID | Quelle |
|------------|------|--------|
| Service-UUID | `00003719-0000-1000-8000-00805f9b34fb` | APK (`BluetoothService.java`) |
| Write (Auth) | `00002345-0000-1000-8000-00805f9b34fb` | APK (`BluetoothService.java`) |
| Read | `00001234-0000-1000-8000-00805f9b34fb` | APK (`BluetoothService.java`) |

---

## 10. Pairing/Bonding (aus APK-Analyse)

Der Pairing-Prozess läuft auf zwei Ebenen:

### 10.1 BLE-Scan & Geräteerkennung

Die App scannt nach BLE-Geräten mit dem Bluetooth‑LeScanner-Standardverfahren:

```kotlin
bluetoothAdapter.getBluetoothLeScanner().startScan(filters, settings, callback)
```

Auf Android-Ebene wird dann der systemeigene Bonding-Prozess angestoßen (Pairing-Dialog mit 6-stelliger PIN, die auf dem Bike-Display angezeigt wird).

### 10.2 Verbindung zu einem bekannten Gerät

```kotlin
fun connectToBike(btDevice: BluetoothDevice, macAddress: String): Observable<ConnectionStatus>
```

Die App verbindet sich zu einem bereits **gebondeten** Gerät. Dazu sucht sie in der Liste der gebondeten Geräte:

```kotlin
val bondedDevices = bluetoothAdapter.getBondedDevices()
for (device in bondedDevices) {
    if (device.address == macAddress) {
        // gefunden, verbinden
    }
}
```

### 10.3 Bond-Status-Überwachung

Die App registriert einen Broadcast-Receiver auf `BOND_STATE_CHANGED`:

```kotlin
intentFilter.addAction("android.bluetooth.device.action.BOND_STATE_CHANGED")
```

Die Verarbeitung:
```kotlin
if (action == "android.bluetooth.device.action.BOND_STATE_CHANGED") {
    val bondState = intent.getIntExtra("android.bluetooth.device.extra.BOND_STATE", ...)
    val prevState = intent.getIntExtra("android.bluetooth.device.extra.PREVIOUS_BOND_STATE", ...)
    val device = intent.getParcelableExtra<BluetoothDevice>("android.bluetooth.device.extra.DEVICE")
    
    if (device.address == bikeBtDevice?.address && 
        bondState == BluetoothDevice.BOND_BONDED && 
        prevState == BluetoothDevice.BOND_BONDING) {
        // Gerät wurde erfolgreich verbunden
    }
}
```

**Zustände:**
| Wert | Konstante | Bedeutung |
|------|-----------|-----------|
| `10` | BOND_NONE | Nicht gekoppelt |
| `11` | BOND_BONDING | Kopplung läuft |
| `12` | BOND_BONDED | Gekoppelt |

### 10.4 Entkopplung (Unbond)

```kotlin
fun disconnectAndUnbond(macAddress: String) {
    // Verbindung trennen
    establishConnectionDisposable?.dispose()
    
    // Bond entfernen
    for (device in bluetoothAdapter.getBondedDevices()) {
        if (device.address == macAddress) {
            removeBond(device)  // via Reflection: device::removeBond()
            bikeBtDevice = null
        }
    }
}
```

Die `removeBond()`-Methode ist eine versteckte Android-API und wird per **Reflection** aufgerufen:

```kotlin
fun removeBond(device: BluetoothDevice) {
    try {
        device.javaClass
            .getMethod("removeBond")
            .invoke(device)
    } catch (e: Exception) {
        Log.e("Remove bond failed: ${e.message}")
    }
}
```

### 10.5 Handshake nach Verbindungsaufbau

Sobald die BLE-Verbindung steht (BOND_BONDED), sendet die App:

1. **HELLO** – Handshake mit Geräte-MAC und Hersteller
2. **GUI1** – GUID-Paar als Session-Keepalive (Start)
3. **VER** – Versionscheck

Erst danach folgen Navigationsdaten.

---

## 11. Technische Hinweise für eine kompatible App

### BLE-Stack-Anforderungen

- **Rolle:** BLE Central (GATT Client)
- **Service:** `00003719-0000-1000-8000-00805f9b34fb`
- **Write (Auth):** `00002345-0000-1000-8000-00805f9b34fb`
- **Write (Navigation):** Handle `0x002D`

### Verbindungsaufbau

1. Nach Peripherals mit Name `BRUTALE_800` (oder `MV*`) scannen
2. Verbinden → System-Pairing-Dialog abwarten (Pincode vom Bike-Display)
3. Service/Characteristic-Discovery
4. `HELLO`-Handshake senden
5. `GUI1`-Keepalive-Paar als Session-Start
6. Periodisch (alle ~3 s) `GUI1`-Keepalive senden

### Wichtige Erkenntnis

Das Motorrad **sendet während der Navigation keine Daten** über BLE. Es ist ein reiner Display-Empfänger.  
Für Telefonie/Musik könnte der Datenfluss anders sein (Notifications vom Bike).

---

## 12. Notification Setup und Bike-zu-Phone-Datenfluss

### 12.1 Notification Setup

Nach erfolgreicher GATT-Dienst- und Characteristic-Entdeckung aktiviert die App Benachrichtigungen auf dem Read-Characteristic (UUID `00001234-0000-1000-8000-00805f9b34fb`). Die Benachrichtigungen werden über `rxBleConnection.setupNotification()` empfangen und als `MultipleDataPacket` geparst (siehe `startListeningUartChannel()`).

### 12.2 Bike → Phone Datenfluss

Während der Navigation sendet das Bike **keine** regelmäßigen Daten. Empfangene Pakete erfolgen nur bei speziellen Anfragen:

* **GPS‑Trip‑Download** – Beim Export einer Strecke sendet das Bike GPS-Rohdaten im Format
  `<0x0D>BCBCBCBC…<0x0D>` (siehe `parseData()`).
* **NEED‑Pakete** – Das Bike kann ein `⏎NEED⏝⏝⏝⏝⏎` senden, um bestimmte Daten (z. B. WLAN‑Konfiguration) anzufordern.
* **Antworten auf IOV‑Anfragen** – Wenn die App eine `IOV`-Nachricht mit WLAN-SSID/Passwort sendet, antwortet das Bike mit einem `IOV`-Paket, das die gefundenen Netze enthält (siehe `parseIovWifi()`).

Alle anderen Richtung‑Nachrichten (Navigationsbefehle, HELLO, GUI1 usw.) werden **nur vom Phone zum Bike** gesendet (Write-With-Response auf Handle `0x002A` bzw. Write-Command auf Handle `0x002D`).

### 12.3 Hinweis zu den Richtungen

Während der aktiven Navigation sendet das Bike **keine** regelmäßigen Updates (keine Geschwindigkeit, Drehzahl usw.). Alle Fahrzustandsdaten müssen über andere Wege (z. B. CAN-Bus) vom Bike abgerufen werden, falls sie benötigt werden.

## 13. Weiteres Vorgehen mit LightBlue (iOS)

### 13.1 Vorbereitung
- **Gerät suchen**: Starte einen Scan, finde **BRUTALE_800** (oder das von dir gebundene Bike).
- **Verbinden**: Tippe auf das Gerät → *Connect*.
- **Services entdecken**: Nach dem Verbinden drücke **Discover Services**.
- **Characteristics auflisten**: Für jedes Service (insbesondere das MV Ride‑Service) klicke auf **Characteristics** und notiere Handles, UUIDs sowie Properties (Read, Write, Notify, Indicate) sowie das CCCD‑Descriptor (UUID 00002902‑…).

### 13.2 Eigenschaften der bekannten Characteristics prüfen
| Characteristic (aus APK) | UUID | Erwartete Properties | Test in LightBlue |
|--------------------------|------|----------------------|-------------------|
| Write‑With‑Response (Auth/Keep‑Alive) | `00002345‑0000‑1000‑8000‑00805f9b34fb` | **Write** (mit Response) | Schreibe ein kurzes Byte‑Array (z. B. `00 01`). Erwarte ein **Write Response** (ATT Opcode 0x13). |
| Write‑Command (Navigationsdaten) | *unbekannt, im Capture nicht sichtbar* | **Write without Response** (Command) | Sende ein bekanntes Kommando (z. B. den `HELLO`‑Befehl: `\rHELLO\x1E<MAC>\x1E<Hersteller>\r`). Erwarte **keine** Antwort (nur ein ATT Write Command ohne Response). |
| Read / Notify (Bike → Phone) | `00001234‑0000‑1000‑8000-00805f9b34fb` | **Read** + **Notify** (evtl. auch **Indicate**) | *Read*: tippe auf das “Read” Symbol und notiere das zurückgelieferte Byte‑Array.<br>*Notify*: aktiviere Benachrichtigungen (Glocke‑Symbol). Beobachte, ob beim Verbinden bereits Daten kommen (z. B. ein erstes `HELLO`‑Echo). Löse verschiedene Aktionen am Bike aus (Navigationsstart, Wegpunkt‑Änderung, Telefon‑/WhatsApp‑Benachrichtigung) und prüfe, ob daraufhin Notify‑Pakete eintreffen. Notiere den Roh‑Hex‑Dump (über das “eye”‑Symbol).<br>*Indicate*: prüfe, ob das Characteristic das *Indicate*‑Flag hat; falls ja, aktiviere ebenfalls Indications und vergleiche, ob du eine Bestätigung (ATT Opcode 0x1e) zurückbekommst. |

### 13.3 MTU‑Austausch testen
- Beim Verbindungsaufbau tauscht der Stack automatisch das MTU aus. In LightBlue lässt sich das aktuelle MTU manchmal unter *Connection Parameters* oder *MTU* einsehen.
- Notiere den Wert (Standard 23 Byte, nach Exchange oft 185 Byte oder 247 Byte).
- Warum wichtig? Ein komplettes Command‑Frame (`<CR>…<CR>`) muss in ein einzelnes ATT‑PDU passen. Ist das MTU zu klein, wird das Paket aufgeteilt → du siehst mehrere ATT‑Pakete im Log. Ein größeres MTU erlaubt längere Payloads (z. B. komplette GPS‑Blöcke) in einem einzigen Write‑Command.

### 13.4 Testen verschiedener Befehlstypen (nach APK‑Enums)
| Befehl (Enum) | ASCII‑Kurzform | Erwarteter Frame (hex) | Was zu testen |
|---------------|----------------|------------------------|----------------|
| `HELLO` | `HELLO` | `0D 48 45 4C 4C 4F 1E <MAC‑ASCII> 1E <Hersteller‑ASCII> 0D` | Schreibe an **Write‑Command** (0x002D). Beobachte, ob danach ein Notify‑Packet vom Bike kommt (evtl. ein `VER` oder `GUI1`). |
| `GUI1` (Keep‑Alive) | `GUI1` | `0D 47 55 49 31 1E 31 1E 31 0D` (zwei Byte‑Parameter nach dem RS) | Sende periodisch (alle ~3 s) und prüfe, ob die Verbindung stabil bleibt (keine unerwarteten Trennungen). |
| `VER` | `VER` | `0D 56 45 52 1E <Version‑String> 0D` | Frage die Firmware‑Version ab (falls das Bike antwortet). |
| `GPS` / `GPS_PARTIAL_DATA` (`G`) | `G` | `0D 47 1E <lat> 1E <lon> 1E <timestamp> 0D` | Simuliere ein GPS‑Paket vom Phone zum Bike (falls das Bike das annimmt) und schaue, ob es irgendwelche Änderungen im Bike‑Display gibt. |
| `IOV` (WLAN‑Konfig) | `IOV` | `0D 49 4F 56 1E <SSID> 1E <PASS> 0D` | Schreibe ein gültiges WLAN‑Credential und prüfe, ob das Bike danach ein `IOV`‑Antwort‑Packet über Notify schickt (siehe unten). |
| `NEED` | `NEED` | `0D 4E 45 45 44 1E <Feld‑1> 1E <Feld‑2> 1E <Feld‑3> 0D` | Löse bewusst einen Zustand aus, bei dem das Bike ein `NEED` erwartet (z. B. WLAN‑SSID falsch oder nicht konfiguriert) und beobachte das eingehende Notify. |
| `MSG` (Benachrichtigung) | `MSG` | `0D 4D 53 47 1E <App‑ID> 1E <Message> 1E <Title> 0D` | Teste mit verschiedenen Apps (WhatsApp, Telegram, SMS) und vergleiche die empfangenen Notify‑Frames. |

**Wie du das Frame baust:** In LightBlue gibt es beim Schreiben ein Textfeld. Gib die hex‑repräsentation ein (z. B. `0D48454C4C4F1E...0D`). Alternativ kann eine App wie “Hex‑Sender” aus dem App‑Store rohe Bytes senden, falls LightBlue nur ASCII zulässt.

### 13.5 Beobachtungen, die du protokollieren solltest
| Beobachtung | Wo festhalten | Bedeutung |
|-------------|---------------|-----------|
| Notify‑Pakete erscheinen nur nach bestimmter Aktion (z. B. nach WhatsApp‑Nachricht) | Notiere Uhrzeit, Aktion und komplettes Hex‑Payload | Bestätigt, welches Event das Bike triggert (z. B. MSG‑Notify). |
| Notify‑Payload beginnt immer mit `0D` und endet mit `0D` | Prüfe erste und letzte Bytes jedes Notify‑Frames | Beweist, dass das gleiche Framing wie bei den Commands (CR‑RS‑…‑CR) verwendet wird. |
| Inhalt zwischen den beiden `0D` besteht aus Feld‑Trennern `0x1E` | Zähle die Anzahl der `0x1E` im Payload | Bestätigt die Feldanzahl (z. B. 3 Trennzeichen → 4 Felder). |
| Einige Notify‑Frames enthalten ausschließlich `BCBCBCBC…` | Notiere Länge und Position im Stream | Das ist das GPS‑Trip‑Download‑Protokoll (siehe Abschnitt 12.2). |
| Beim Schreiben eines gültigen `IOV`‑Commands kommt ein Notify‑Frame mit `IOV` zurück | Vergleiche gesendetes und empfangenes Payload | Bestätigt die Request/Response‑Semantik von IOV. |
| Keine Antwort auf ein Write‑Command (kein Notify, kein Write Response) | Notiere, dass das Kommando ein „fire‑and‑forget“ ist | Bestätigt, dass das Characteristic wirklich *Write without Response* ist. |
| Beim Lesen des Read‑Charakters bekommst du ein 0‑Byte‑ oder ein bestimmtes Start‑Packet | Notiere das exakt zurückgelesene Byte‑Array | Könnte ein Initial‑State (z. B. leeres `GPS_PARTIAL_DATA`) sein – hilft beim Zustandsmodell. |

### 13.6 Optional: Kombiniere LightBlue mit einem Paket‑Sniffer
Wenn du einen **Bluetooth‑Sniffer** (z. B. Adafruit Bluefruit LE Sniffer, Frontline, oder einen Ubertooth) hast, kannst du gleichzeitig:
1. **Den Bluetooth‑Link mit dem Sniffer aufzeichnen** (liefert das niedrigste ATT‑Level, inkl. Handle‑Nummern, OpCodes, ATT‑MTU‑Exchange).
2. **Parallel in LightBlue die gleichen Aktionen ausführen** (schreiben, notify‑aktivieren, etc.).
3. **Die beiden Logs korrelieren** – du erhältst sowohl die *hoch‑level* Bedeutung (welches Kommando du gesendet hast) als auch die *low‑level* Sicht (wie das ATT‑Layer es tatsächlich auf die Luft bringt – inkl. eventuelle Aufteilung wegen MTU, Bestätigungen usw.).

Diese Kombination ist besonders nützlich, um zu bestätigen, ob ein „Write without Response“ wirklich nur ein ATT‑Write‑Command (Opcode 0x52) ist oder ob das Gerät dennoch ein Antwort‑Packet schickt (was dann ein Missverständnis im Protokollmodell wäre).

### 13.7 Was du danach in die Dokumentation eintragen kannst
| Abschnitt | Ergänzung (Beispiel) |
|-----------|----------------------|
| **2. GATT‑Profil** | Vollständige Tabelle mit **Properties** und **CCCD‑Handle** für jedes Characteristic (z. B. Handle 0x0015 = CCCD für Notify‑Char). |
| **3. Nachrichtenformat** | Für jedes Kommando (HELLO, GUI1, VER, IOV, NEED, MSG, GPS_PARTIAL_DATA) das genaue Byte‑Layout samt Beispiel‑Hex‑Frames. |
| **4.16 `MSG`** | Aktualisierte Beschreibung (siehe oben) inkl. Felder *applicationId*, *message*, *title* und Hinweis zur Firmware‑abhängigen Anzeige. |
| **12. Notification Setup und Bike‑zu‑Phone‑Datenfluss** | Detaillierte Schritte zum Notify‑Setup, erwartete Payloads für GPS‑Trip‑Download, NEED‑Pakete und IOV‑Antworten. |
| **13. Weiteres Vorgehen mit LightBlue (iOS)** | Dieser Abschnitt selbst – dient als To‑Do‑Liste für weitere Untersuchungen mit einem iOS‑Gerät. |

### 13.8 Weiteres Vorgehen
1. Führe die oben genannten Schritte systematisch durch.
2. Protokolliere jede Beobachtung in einem eigenen Log‑File (z. B. `observations_lightblue.md`).
3. Nach Abschluss der Analyse aktualisiere die Spezifikation (Abschnitte 2, 3, 4.16, 12) mit den neuen Erkenntnissen.
4. Versioniere die Dokumentation im Änderungsprotokoll hoch (z. B. v3.0).

--- 

## 📝 Änderungsprotokoll

| Datum | Version | Änderungen |
|-------|---------|------------|
| 2026-07-16 | v1.0 | Erstfassung basierend auf `mvride_nav.pklg`-Capture |
| 2026-07-16 | v2.0 | APK-Dekompilierung (jadx) – alle 16 Nachrichtentypen, UUIDs, Turn-Enum, Pairing-Mechanismus |
| 2026-08-27 | v3.0 | Ergänzung Abschnitt 13: Weiteres Vorgehen mit LightBlue (iOS) – detaillierte Test‑ und Beobachtungsanleitung für BLE‑Analyse mit iPhone. |

