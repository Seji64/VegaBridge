# BLE Plugin Architecture Design

## Overview
This document defines the architecture for the BLE Plugin system in the VegaBridge app. The goal is to move all manufacturer-specific communication logic into isolated plugins, decoupling the core service from the hardware protocol and the underlying BLE stack.

## Architecture

### 1. Hardware Abstraction Layer (HAL)
To avoid dependency on a specific BLE library (e.g., Shiny), we introduce `IBleConnectedDevice`. This allows plugins to communicate with hardware without knowing the underlying implementation.

**`IBleConnectedDevice`**
- `Guid Uuid { get; }`
- `string Name { get; }`
- `Task WriteAsync(string characteristicUuid, byte[] data, bool withResponse)`
- `Task<byte[]> ReadAsync(string characteristicUuid)`

The `BleManagerService` (or a dedicated wrapper) implements this interface, mapping these calls to the actual `IPeripheral` methods.

### 2. Plugin Interface (`IBleDevicePlugin`)
Plugins are now functional drivers rather than simple frame builders.

**`IBleDevicePlugin`**
- **Identification**
    - `string ManufacturerId { get; }`
    - `string DisplayName { get; }`
    - `bool IsCompatible(BleDeviceInfo device)`: Logic to determine if the plugin supports the device (e.g., name matching, service UUID check).
- **Communication**
    - `Task SendAsync(IBleConnectedDevice device, string command, params string[] fields)`: High-level command sending. Handles frame building and transmission via the HAL.
    - `Task SendTestAsync(IBleConnectedDevice device)`: Implements the specific "Test Frame" logic to verify connectivity.
    - `void OnDataReceived(byte[] data)`: Handler for all incoming notifications.
- **Protocol Definitions**
    - `Guid ServiceUuid { get; }`
    - `string ControlWriteCharacteristicUuid { get; }`
    - `string ReadCharacteristicUuid { get; }`

### 3. Service Orchestration (`BleManagerService`)
The service manages the lifecycle and routing.

- **Plugin Registry**: Injects `IEnumerable<IBleDevicePlugin>` via DI.
- **Plugin Selection**: Upon connection, it selects the first plugin where `IsCompatible(device)` is true.
- **Notification Routing**: Forwards all incoming bytes from the BLE stack to `_activePlugin.OnDataReceived(data)`.
- **Command Routing**: Proxies UI requests (e.g., "Send Test") to the `_activePlugin`.

## Data Flow

### Outbound (UI $\rightarrow$ Device)
`Settings.razor` $\rightarrow$ `BleManagerService.SendTest()` $\rightarrow$ `_activePlugin.SendTestAsync(deviceWrapper)` $\rightarrow$ `deviceWrapper.WriteAsync(...)` $\rightarrow$ `Shiny.BluetoothLE` $\rightarrow$ Hardware.

### Inbound (Device $\rightarrow$ UI)
Hardware $\rightarrow$ `Shiny.BluetoothLE` $\rightarrow$ `BleManagerService` $\rightarrow$ `_activePlugin.OnDataReceived(data)` $\rightarrow$ (Internal Plugin Logic/State Update) $\rightarrow$ (Optional: Service State/Device List update) $\rightarrow$ `Settings.razor`.

## Error Handling & Edge Cases
- **No Compatible Plugin**: If no plugin is compatible, the service remains in `Connected` state but notifies the user that no driver is available for this device.
- **Disconnection during Send**: `IBleConnectedDevice.WriteAsync` will throw an exception if the device is disconnected; plugins should handle this or let it bubble up to the service for general error handling.
- **Wrong Plugin Selected**: If a user connects to a device that is misidentified, the plugin's `OnDataReceived` or `SendAsync` should fail gracefully.

## Scalability
Adding a new manufacturer (e.g., "KTM") requires only:
1. Creating a new class `KtmBlePlugin` implementing `IBleDevicePlugin`.
2. Registering the class in the DI container.
3. No changes to `BleManagerService` or the UI are required.
