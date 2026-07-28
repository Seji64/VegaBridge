using Shiny.BluetoothLE;

namespace VegaBridgeApp.Services.BLE;

/// <summary>
/// Custom BLE delegate for handling adapter and peripheral state changes.
/// </summary>
public class CustomBleDelegate : BleDelegate
{
    public override Task OnAdapterStateChanged(AccessState state)
    {
        System.Diagnostics.Debug.WriteLine($"[BLE] Adapter state changed: {state}");
        return Task.CompletedTask;
    }

    public override Task OnPeripheralStateChanged(IPeripheral peripheral)
    {
        System.Diagnostics.Debug.WriteLine($"[BLE] Peripheral state changed: {peripheral.Name} = {peripheral.Status}");
        return Task.CompletedTask;
    }
}