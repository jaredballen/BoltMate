namespace LogiPlusSwitcher.Hid.Abstractions;

/// <summary>
/// USB and HID identifiers for the Logitech Bolt receiver.
/// </summary>
public static class BoltConstants
{
    /// <summary>Logitech vendor id.</summary>
    public const ushort LogitechVendorId = 0x046D;

    /// <summary>Logitech Bolt receiver product id.</summary>
    public const ushort BoltReceiverProductId = 0xC548;

    /// <summary>HID Usage Page for the receiver's HID++ management interface (vendor-defined).</summary>
    public const ushort ManagementUsagePage = 0xFF00;

    /// <summary>HID Usage value on the management interface.</summary>
    public const ushort ManagementUsage = 0x0001;
}
