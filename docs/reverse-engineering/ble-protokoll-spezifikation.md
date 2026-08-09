# MV Agusta Brutale 800 — BLE Protokoll Spezifikation

> **Stand:** 2026-08-09  
> **Version:** 4.0  
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
| `0x002A` | Write mit Response | `00002345-0000-1000-8000-00805f9b34fb` | GUI1 (Auth Keepalive, Bike→Phone Notify) |
| `0x002D` | Write Command | *(im Capture nicht sichtbar)* | Alle Navigationsbefehle |
| ? | Notify/Read | `00001234-0000-1000-8000-00805f9b34fb` | Bike → Phone (GPS-Download, NEED) |

**Anmerkung:** Die UUIDs stammen aus der APK-Dekompilierung (`BluetoothService.java`).  
**Datenfluss:** Primär **Phone → Bike** (Navigationsbefehle). Das Bike sendet Daten über BLE-Notifications (Read-Characteristic `00001234-...`), z. B. GPS-Trip‑Downloads und NEED‑Anfragen.

> **Hinweis:** Da die Service Discovery vor Capture-Beginn stattfand, sind die UUIDs nicht im `.pklg` sichtbar. Zur Sicherheit sollten sie via nRF Connect / LightBlue direkt am Bike verifiziert werden.

**Wichtige Hinweis zu den Write-Modes:**
- **Handle `0x002A` (GUI1 Keepalive):** Muss mit **Write mit Response** (`withResponse: true`) angesprochen werden.
- **Handle `0x002D` (alle Navigationsbefehle wie HELLO, VER, GPS, IOV, NEED, ORIG, DEST, REM, NAVI, SM, SM1, RENAVI, FINISH, G, MSG, PING):** Muss mit **Write ohne Response** (`withResponse: false`) angesprochen