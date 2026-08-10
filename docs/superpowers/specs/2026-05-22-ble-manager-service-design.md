# Design: BleManagerService Re-implementation

## Goal
Replace the obsolete `BleManagerService` with a modern, reactive implementation using `Shiny.BluetoothLE`. The initial focus is on robust device discovery (scanning) and connection management, designed for seamless integration with a Blazor UI (`Settings.razor`).

## Architecture: Reactive Wrapper
The service acts as a state machine that wraps the low-level Shiny BLE APIs. Instead of disjointed events, it exposes a unified state stream and a clean data model.

### 1. State Model
The service manages a global `BleState` to ensure the UI can react predictably.

| State | Description | UI Implication |
| :--- | :--- | :--- |
| `Idle` | Ready for action. | Show "Start Scan" / "Connect" buttons. |
| `Scanning` | Actively searching for peripherals. | Show loading indicator, disable "Start Scan". |
| `Connecting` | Handshaking with a selected device. | Show "Connecting..." spinner, disable all BLE controls. |
| `Connected` | Stable connection established. | Show "Connected" status and "Disconnect" button. |
| `Error` | An operation failed. | Show error message/snackbar, return to `Idle`. |

### 2. Data Model: `BleDeviceInfo`
To decouple the UI from the heavy `IPeripheral` objects, a lightweight POCO is used:
- `Uuid`: `Guid` - The unique device identifier.
- `Name`: `string` - The advertised name (defaults to "Unknown").
- `IsConnected`: `bool` - Current connection status.
- `LastSeen`: `DateTime` - Timestamp of the last advertisement.

### 3. Core Logic & Workflow

#### Discovery (Scanning)
- **Tooling**: Uses `IManagedScan` for automatic deduplication and peripheral list management.
- **Process**: 
    1. Verify/Request BLE permissions via `IBleManager`.
    2. Transition state to `Scanning`.
    3. Start the managed scan.
    4. Map discovered `IPeripheral` objects into the `BleDeviceInfo` collection.
    5. Stop scan via `StopScan()` or timeout, transitioning back to `Idle`.

#### Connection
- **Process**:
    1. Transition state to `Connecting`.
    2. Resolve `IPeripheral` by UUID.
    3. Call `peripheral.ConnectAsync()` with a 30s timeout.
    4. On success: Transition to `Connected` and store the active peripheral.
    5. On failure: Transition to `Error`.

#### Disconnection
- Call `peripheral.DisconnectAsync()`.
- Clear active peripheral reference.
- Transition to `Idle`.

## UI Integration (`Settings.razor`)
The UI will interact with the service via:
- `IObservable<BleState> State`: To drive button visibility and loading states.
- `IObservable<IReadOnlyList<BleDeviceInfo>> Devices`: To render the list of found devices.
- `Task ConnectAsync(Guid uuid)`: To trigger the connection flow.
- `Task DisconnectAsync()`: To terminate the session.

## Future-Proofing
While currently showing all devices, the `StartScan` logic is designed to accept a list of `ServiceUuids`. This allows for a trivial transition to plugin-based filtering (e.g., only showing MvAgusta devices) by passing the plugin UUIDs into the `ScanConfig`.

## Error Handling
- All GATT operations and connection attempts are wrapped in `try-catch` blocks.
- `BleException` and `OperationCanceledException` are caught and mapped to the `Error` state.
- Errors are surfaced to the UI via a dedicated `ErrorMessage` property or a separate event.
