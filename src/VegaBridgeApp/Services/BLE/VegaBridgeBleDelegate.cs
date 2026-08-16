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
/// </summary>
#if IOS || MACCATALYST
public class VegaBridgeBleDelegate : BleDelegate
{
    public override Task OnAdapterStateChanged(AccessState state)
    {
        Log.Debug("BLE adapter state changed: {State}", state);
        return Task.CompletedTask;
    }

    public override Task OnPeripheralStateChanged(IPeripheral peripheral)
    {
        Log.Debug("BLE peripheral state changed: {Uuid} -> {Status}", peripheral.Uuid, peripheral.Status);
        return Task.CompletedTask;
    }
}
#endif
