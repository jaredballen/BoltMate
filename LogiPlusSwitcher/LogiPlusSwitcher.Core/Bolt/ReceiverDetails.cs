namespace LogiPlusSwitcher.Core.Bolt;

/// <summary>
/// Receiver-level metadata pulled from HID++ 1.0 registers. Populated by
/// <see cref="BoltReceiver.GetReceiverDetailsAsync"/> on demand.
/// </summary>
/// <param name="Serial">12-hex-character serial extracted from <c>BOLT_UNIQUE_ID</c> (register 0xFB). Null if the read failed.</param>
/// <param name="FirmwareVersionMajor">Receiver firmware major version, from <c>RECEIVER_INFO</c> sub-register 0x03.</param>
/// <param name="FirmwareVersionMinor">Receiver firmware minor.</param>
/// <param name="FirmwareBuild">Receiver firmware build (big-endian short).</param>
/// <param name="MaxDevices">Maximum paired devices the receiver supports (6 for Bolt).</param>
/// <param name="BluetoothAddress">Receiver's own BLE address (raw bytes, MSB first). Null if not exposed.</param>
public sealed record ReceiverDetails(
    string? Serial,
    byte FirmwareVersionMajor,
    byte FirmwareVersionMinor,
    ushort FirmwareBuild,
    byte MaxDevices,
    byte[]? BluetoothAddress)
{
    public string FirmwareVersionString => $"{FirmwareVersionMajor:X2}.{FirmwareVersionMinor:X2}.B{FirmwareBuild:X4}";

    public string? BluetoothAddressString =>
        BluetoothAddress is null
            ? null
            : string.Join(":", BluetoothAddress.Select(b => b.ToString("X2")));

    public override string ToString() =>
        $"serial={Serial ?? "?"} fw={FirmwareVersionString} maxDevices={MaxDevices} ble={BluetoothAddressString ?? "?"}";
}
