using Serilog;
using Shiny.BluetoothLE;

namespace VegaBridgeApp.Services.BLE;

/// <summary>
/// Apple (iOS/Mac Catalyst) BLE delegate for background event delivery.
///
/// Enables CoreBluetooth State Restoration (via AppleBleConfiguration in
/// MauiProgram.cs): iOS can wake the app for BLE events even after it was
/// suspended, instead of silently dropping the connection while the phone
/// is in the pocket with the screen off.
///
/// Docs (Shiny.BluetoothLE): "Adapter state changes typically happen when
/// your app is fully backgrounded … Observable subscriptions don't survive
/// app sleep/restart cycles. Use IBleDelegate.OnAdapterStateChanged instead
/// – it fires reliably even when your app restarts in the background."
/// So adapter-level state is handled HERE (forwarded to BleManagerService),
/// not via WhenStatusChanged() subscriptions.
/// </summary>
#if IOS || MACCATALYST
public class VegaBridgeBleDelegate(BleManagerService bleService) : BleDelegate
{
    public override Task OnAdapterStateChanged(AccessState state)
    {
        // Runs off the main thread; the service handler only syncs UI state
        // and must never throw into Shiny's delegate pipeline.
        _ = Task.Run(() =>
        {
            try
            {
                bleService.OnBleAdapterStateChanged(state);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "BLE adapter state handler failed ({State})", state);
            }
        });
        return Task.CompletedTask;
    }

    public override Task OnPeripheralStateChanged(IPeripheral peripheral)
    {
        Log.Debug("BLE peripheral state changed: {Uuid} -> {Status}", peripheral.Uuid, peripheral.Status);
        return Task.CompletedTask;
    }
}
#endif
