# Follow-up-Plan: BLE W2R-Resilienz (Nachfolger von PR #28)

> Status: **Planung** – Zweig `fix/ble-w2r-resilience`, Implementierung erst nach Freigabe.
> Basis: Revert von PR #28 (`8b23338`), TestFlight Build 8 = 1.0.4 (8) = exakte Vor-PR-Baseline.

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
| 4 | **Full-Teardown-Eskalation bei anhaltendem W2R-Stall** | `BleManagerService` (`InvalidateConnectionAndReconnect` + `RetryConnectionAsync`) | Stufenmodell: 3 consecutive W2R-Timeouts (~12 s) → heutiger Reconnect (15 s Cooldown, 3× 2^n s); ab 6 consecutive Fails bzw. 60 s ohne erfolgreichen Write → **Disconnect + Peripheral-Objekt-Release + Re-Scan + frische Verbindung**. Log-Linie pro Eskalation. |
| 5 | **Force-Sync nach Reconnect** | `BleManagerService` / Plugin | Nach jedem (Re-)Connect/Teardown: aktuellen NAVI+SM sofort mit `force=true` senden, damit das Display synchron ist (kompensiert die unter 5.7.2 verlore Frames im Stall-Fenster). |
| 6 | **Shiny 5.7.2 + On-Change-Policy neu einbringen** | (Revert-Reintroduction) | Erst NACH 1–5; #1657-Fix für 09-23-artige Deadlocks bleibt dabei erhalten. Falls Full-Teardown in der Testtour nicht ausreicht: Shiny-5.4.0-Revert als Plan B. |
| 7 | **Doku-Update** | `docs/.../ble-protokoll-spezifikation.md` | §6.1 (official traffic profile) neu + neuer Abschnitt „Tour 09-26: W2R-TX-Stall, sessionscope, Root-Cause-Korrektur“ (Changelog v4.4). |
| 8 | **Kosmetik: Serilog-Bool-Format** | `BleNavigationCoordinator` | „backup in Trues/Falses“ (F0-Format an Bool in de-DE-Culture) → saubere Template-Ausgabe. |
| 9 | **Version** | `VegaBridgeApp.csproj` | 1.0.4 Build 9 für nächste TestFlight-Runde. |
| 10 | **W2R-Stress-Test-Button (implementiert ✅)** | `BleManagerService.RunW2rStressTestAsync`, `Settings` | Settings-Button „W2R Test (60×)“: sendet 60 Ticks (~1 Hz) den echten Nav-Frame-Flow (NAVI+SM, „W2R Test“ auf dem Display sichtbar). Jeder Failure-Tick + Summary landen im Log unter `BLE-WRITE-TEST` / `W2R STRESS TEST`. UI zeigt die Summary (ok/fail-Count, first-fail-Tick, max tick-ms). |

## Entschieden (2026-09-26, User-Feedback)

- **Kein RSSI-Sampling:** Verbindungstärke ist im Bike-Display sichtbar und dort konstant gut → RF-Interferenz wird nicht weiter als Arbeitshypothese verfolgt.
- **Kein Cross-Test mit der offiziellen App / keine FW-Fehlermeldung an MV Agusta:** Die offizielle App funktioniert, die Bike-FW gilt als sauber. „Warum stallt der W2R-Consumer ab?“ wird nicht weiter über die Firmware-Hypothese verfolgt, sondern über den Stress-Test (Punkt 10) operational abgefangen: reproduzierbarer Stall → Trigger identifiziert; nie reproduzierbar stationär → Resilienz-Features (4+5) sind die Antwort.
- **Wichtig (5.4.0-Asymmetrie):** Unter Shiny 5.4.0 (aktuelle Baseline) queueen Writes in einen gestauten W2R-Pfad statt Timeout zu werfen → der Stress-Test liest dann „0 failed“, selbst wenn das Display stale ist. Den Test als Detektor kann man erst mit Shiny 5.7.2+ (Punkt 6) nutzen; als **Reproduktions-Trigger** (kriegt der 1-Hz-Flow den Konsum gar erst ins Stallen?) funktioniert er in beiden Versionen.

## Testprotokoll (nach Implementierung)

0. **W2R-Stress-Test (Settings, vor der Tour, stationär):** 1× „W2R Test (60×)“ ausführen.
   - 60/60 ok → Link gesund, Tour starten.
   - early fails (z. B. ab Tick 30) → Trigger-Pattern gefunden (Rate/Pattern-basiert) → „first fail tick“-Kennzahl in den Log.
   - 60/60 ok, aber die Tour stallt trotzdem → Trigger braucht Fahrbetrieb (Ort/RF-Umfeld) → Resilienz-Features (4+5) tragen die Last.
1. Kurze Testtour 20–30 min: mindestens 1 Nahbereich (< 2 km bis Manöver), 1 Stand ≥ 60 s, 1 Autobahn-Section.
2. Prüfen:
   - Nahbereich (fahrend): Skip-Rate > 50 %, keine 1-Hz-Sendflut (Jitter-Hysterese wirkt).
   - PING: exakt alle 30 s, auch während 1-Hz-Nav-Bereichen.
   - „BLE link degraded“ erscheint bei Write-Stall (Watchdog fix).
   - Full-Teardown: tritt bei anhaltendem Stall auf und klärt TX **ohne** App-Neustart.
3. Log-Export + die 6 Kennzahlen (Write-Failures, PING-Ok/Ratio, Reconnect-Zyklen, Send/Skip-Rate, Gate-Busy, Teardown-Ereignisse).

## Offene Fragen / Risiken

- **Full-Teardown während der Fahrt:** kurzzeitig (~5–10 s) verlore Dashboard-Link (GUI1-RX). Akzeptabel? Force-Sync (Punkt 5) kompensiert danach.
- **Frame-Verlust unter 5.7.2 bleibt im Stall-Fenster inhärent** (Timeout statt Queue) – Display zeigt veraltete Anweisungen bis zur Erholung; Force-Sync + 30-s-Backup minimieren das.
- **Shiny-Entscheidung** wird erst nach Testtour getroffen (5.7.2 + Teardown vs. 5.4.0-Revert).
