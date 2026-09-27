# Follow-up-Plan: BLE W2R-Resilienz (Nachfolger von PR #28)

> Status: **Planung** – Zweig `fix/ble-w2r-resilience` (PR #29), Implementierung erst nach Freigabe.
> Zweig-Stand: 1-Hz-Baseline + W2R-Stress-Test-Button + **Shiny 5.7.2** + Package-Updates (User-Commits `2216811`, `141adf9`, `17a2bf2`). main = saubere Revert-Baseline (Shiny 5.4.0).

## Hintergrund – Tour 2026-09-26 (zwei Logs)

| Befund | Log 1 (13:58–14:34) | Log 2 (15:22–16:13) |
|---|---|---|
| W2R-Write-Failures | **354** consecutive (`Arg_TimeoutException`, ~4 s je Versuch) | **0** |
| PING (30 s) | 15 gesendet, **14/15 fehlgeschlagen** ( starvation: 5-s-Skip-Fenster) | 87/87 ok, exakt alle 30 s |
| In-Session-Reconnects | 14 Zyklen – ** keiner hat den Stall geklärt** | 0 |
| GUI1-RX (Bike→Phone) | 3–4 Hz durchgehend am Leben (bis 14:33:44) | normal |
| Top-Speed | – | 104 km/h, W2R fehlerfrei |
| On-Change-Policy | im Nahbereich durch GPS-Jitter neutralisiert (~1 Hz: 50 Sends + 40 Gate-Busy + 13 Fails/min) | **funktioniert**: 263 Skips vs 168 Sends (~40 % weniger Traffic) |

**Root Cause (final):**
1. **Primär:** Bike-/Stack-seitiger W2R-TX-Stall ab 14:02:23 (min. 31 min), aufgetreten bei 22 km/h im Stadtverkehr (nicht B10/Hochgeschwindigkeit – die Hochgeschwindigkeits-Hypothese ist damit widerlegt). RX lief weiter; der Link blieb „connected“.
2. **Verstärker 1 (App):** Shiny 5.7.2 timet W2R-Timeouts aus und verliert Frames, wo 5.4.0 gequeuet und spät geliefert hat → aus „verzögerte Updates“ wurde „totales Einfrieren“.
3. **Verstärker 2 (App):** GPS-Snap-Jitter (distTurn 647↔663 m, Snap 1073↔1074) flippt den 50-m-Bucket pro Tick → On-Change-Policy degeneriert im Nahbereich zu ~1 Hz; PING-Starvation deaktiviert das Health-Signal.

**Entscheidender Befund aus Log 2:** Der Stuck-Zustand ist **sessionscope**: 14 In-Session-Reconnects (gleiches Peripheral-Objekt, neuer GATT-Link) haben ihn nicht geklärt, aber ein vollständiger Anhalt + App-Neustart (neuer Scan, neues Peripheral-Objekt) hat ihn vollständig geklärt – danach 51 min fehlerfreier Betrieb. → Ein **Full-Teardown (Disconnect + Objekt-Release + Re-Scan + frische Verbindung)** ist der wirksame Reset-Primitive.

## Änderungsliste

| # | Änderung | Datei(en) | Detail |
|---|---|---|---|
| 1 | **Jitter-Hysterese auf Bucket-Trigger** | `BleNavigationCoordinator` | Bucket-Flip löst erst nach 2 aufeinanderfolgenden Ticks aus + ±10 % Hysterese-Band an der Bucket-Grenze (50-m-Zone). 30-s-Backup-Resend bleibt. |
| 2 | **PING entkoppeln** | `MvAgustaBlePlugin` | PING-Timer läuft unabhängig von Nav-Updates: 5-s-Skip-Fenster nach Nav-Update entfernen (PING = 8-Byte-Frame, kostenneutral). PING muss auch bei 1 Hz Nav-Updates zuverlässig alle 30 s ankommen. |
| 3 | **Watchdog verifizieren** | `BleManagerService` | „BLE link degraded“ steht im 09-26-Log **0-mal** trotz 354 Fehlern – `ReportNavWriteStale()` nachverfolgen (Logger-Kategorie? DebugLogSink-Export-Filter?), fixen und in Testtour prüfen. |
| 4 | ~~**Full-Teardown-Eskalation**~~ | – | **Ablehnt (User):** automatisches Disconnect+Release+Re-Scan bei anhaltendem Stall = „Windows neu installieren, wenn es hakt“. Falsche Kategorie; kein Auto-Eskalation. |
| 5 | ~~**Force-Sync nach Reconnect/Teardown**~~ | – | **Ablehnt (User):** mit Punkt 4 raus. Bei 1 Hz ist der Resync nach Erholung ohnehin automatisch (nächstes Update ≤ 1 s). |
| 6 | **Shiny: kein Pinning auf 5.4.0, Zweig bleibt auf aktuellem 5.x (5.7.2)** | `VegaBridgeApp.csproj` | User-Entscheidung: „wir können keine App entwickeln, wo wir eine ur-alt Version pinnen müssen“. On-Change-Policy (Punkte 1+2) bleibt aufgeschoben; Shiny-Package-Bump steht bereits (User-Commits). |
| 6a | **(optional) Manueller „BLE-Session-Reset“-Button** | `BleManagerService`, `Settings` | Nur wenn Watchdog „degraded“ meldet: manueller Button (Disconnect + Objekt-Release + Re-Scan + Connect). Keine Auto-Eskalation – der Reset bleibt beim User. **Freigabe ausstehend.** |
| 7 | **Doku-Update** | `docs/.../ble-protokoll-spezifikation.md` | §6.1 (official traffic profile) neu + neuer Abschnitt „Tour 09-26: W2R-TX-Stall, sessionscope, Root-Cause-Korrektur“ (Changelog v4.4). |
| 8 | **Kosmetik: Serilog-Bool-Format** | `BleNavigationCoordinator` | „backup in Trues/Falses“ (F0-Format an Bool in de-DE-Culture) → saubere Template-Ausgabe. |
| 9 | **Version** | `VegaBridgeApp.csproj` | 1.0.4 Build 9 für nächste TestFlight-Runde. |
| 10 | **W2R-Stress-Test-Button (implementiert ✅)** | `BleManagerService.RunW2rStressTestAsync`, `Settings` | Settings-Button „W2R Test (60×)“: sendet 60 Ticks (~1 Hz) den echten Nav-Frame-Flow (NAVI+SM). Der Payload zählt mit (Anweisung „W2R Test N/60“, SM-Abzählung 998→939 m) → am Ende steht die erreichte Delivery auf dem Display. Jeder Failure-Tick + Summary landen im Log unter `BLE-WRITE-TEST` / `W2R STRESS TEST`. UI zeigt die Summary (ok/fail-Count, first-fail-Tick, max tick-ms). |

## Entschieden (2026-09-26, User-Feedback)

- **Kein RSSI-Sampling:** Verbindungstärke ist im Bike-Display sichtbar und dort konstant gut → RF-Interferenz wird nicht weiter als Arbeitshypothese verfolgt.
- **Kein Cross-Test mit der offiziellen App / keine FW-Fehlermeldung an MV Agusta:** Die offizielle App funktioniert, die Bike-FW gilt als sauber. „Warum stallt der W2R-Consumer ab?“ wird nicht weiter über die Firmware-Hypothese verfolgt.
- **Kein automatischer Full-Teardown (User):** automatischer Session-Reset bei anhaltendem Stall wird abgelehnt. Stattdessen: **Watchdog + UI-Status** (Punkt 3) → User sieht den degradieren Zustand und entscheidet (manueller App-/Session-Reset im Stillstand). Optionaler manueller Reset-Button = Punkt 6a.
- **Kein Shiny-Pinning auf 5.4.0 (User):** Zweig fährt aktuelles Shiny (5.7.2). Konsequenz: W2R-Stall unter 5.7.x = Frame-Verlust (Timeout statt Queue) → Freeze während des Stalls ist das akzeptierte Verhalten; abgemildert durch Watchdog + auto-Resync (1 Hz) nach Erholung.
- **Stress-Test wurde bewusst mit Shiny 5.7.0 getestet (User):** 60/60 ok, max. Drain 11 ms, max. Tick 207 ms → Rate-Trigger **ausgeschlossen**, W2R-Pfad stationär gesund. Test gilt als Pre-Tour-Health-Check (5.7.x-Semantik: 4-s-Timeouts = Detektoren).

## Testprotokoll (nach Implementierung)

0. **W2R-Stress-Test (Settings, vor der Tour, stationär):** 1× „W2R Test (60×)“ ausführen.
   - 60/60 ok **und** am Test-Ende zeigt das Display „W2R Test 60/60“ / ~939 m → alle Frames geliefert, Link gesund, Tour starten.
   - 60/60 ok, aber Display steht auf z. B. „37/60“ → lokaler Write-OK, **Delivery-Stall** (5.4.0-Queueing): ab Tick 37 kamen die Frames nicht mehr an.
   - early fails (z. B. ab Tick 30) → Trigger-Pattern gefunden (Rate/Pattern-basiert) → „first fail tick“-Kennzahl in den Log.
   - 60/60 ok, aber die Tour stallt trotzdem → Trigger braucht Fahrbetrieb (Ort/RF-Umfeld) → Watchdog + manueller Reset tragen die Last.
1. Kurze Testtour 20–30 min: mindestens 1 Nahbereich (< 2 km bis Manöver), 1 Stand ≥ 60 s, 1 Autobahn-Section.
2. Prüfen:
   - „BLE link degraded“ erscheint bei Write-Stall (Watchdog, Punkt 3) – UI-Status **und** im Log-Export sichtbar.
   - (falls 6a) manueller Reset-Button klärt den W2R-Stall ohne App-Neustart.
3. Log-Export + Kennzahlen: Write-Failures, PING-Ok/Ratio, Reconnect-Zyklen, degraded-Ereignisse, ggf. manueller Reset.

## Offene Fragen / Risiken

- **Frame-Verlust unter 5.7.x bleibt im Stall-Fenster inhärent** (Timeout statt Queue) – Display zeigt veraltete Anweisungen bis zur Erholung; Resync passiert automatisch (1 Hz ≤ 1 s nach Erholung) oder per manueller Session-/App-Reset.
- **Manueller Reset-Button (6a):** kurzzeitig (~5–10 s) wegfallender Dashboard-Link (GUI1-RX) während des Re-Scans. Akzeptabel?
- **Stall-Dauer:** am 09-26 31 min ohne Erholung – ohne Teardown ist der einzige Heilweg der manuelle (Session-/App-)Reset. Watchdog muss den Zustand also früh und klar anzeigen.
