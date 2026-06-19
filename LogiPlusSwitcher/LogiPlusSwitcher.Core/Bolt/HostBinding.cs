namespace LogiPlusSwitcher.Core.Bolt;

/// <summary>
/// One row of a device's per-host pairing table — what receiver (by BLE
/// address) the device will reach when the user switches it to a given host
/// slot. Read from HID++ 2.0 feature 0x1815 HOSTS_INFO fn 0x10 getHostInfo.
/// </summary>
/// <param name="HostIndex">Zero-indexed host slot on the device (0..2 for most Logi devices).</param>
/// <param name="Paired">True if this slot has a paired receiver.</param>
/// <param name="BluetoothAddress">6-byte BLE address of the paired receiver (MSB first). Null if the slot is unpaired or the address was not legible.</param>
/// <param name="ReceiverName">Friendly name of the host as stored on the device (set by Logi Options+ or our own pairing flow).</param>
public sealed record HostBinding(
    byte HostIndex,
    bool Paired,
    byte[]? BluetoothAddress,
    string? ReceiverName)
{
    /// <summary>Stable lowercase hex string of <see cref="BluetoothAddress"/>, suitable as a dictionary key.</summary>
    public string? BluetoothAddressKey =>
        BluetoothAddress is null ? null : Convert.ToHexString(BluetoothAddress).ToLowerInvariant();

    /// <summary>Standard colon-separated MAC-style rendering for display.</summary>
    public string? BluetoothAddressString =>
        BluetoothAddress is null
            ? null
            : string.Join(":", BluetoothAddress.Select(b => b.ToString("X2")));
}
