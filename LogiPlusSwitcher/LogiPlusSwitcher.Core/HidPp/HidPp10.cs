namespace LogiPlusSwitcher.Core.HidPp;

/// <summary>
/// HID++ 1.0 receiver-level register helpers. The Bolt receiver still speaks
/// HID++ 1.0 for housekeeping commands (enable notifications, enumerate paired
/// devices) — those are fire-and-forget writes addressed to device_index 0xFF.
/// </summary>
public static class HidPp10
{
    /// <summary>Sub-id 0x80 SET_REGISTER (short request from host to receiver).</summary>
    public const byte SubIdSetRegister = 0x80;

    /// <summary>Sub-id 0x81 GET_REGISTER.</summary>
    public const byte SubIdGetRegister = 0x81;

    /// <summary>HID++ 1.0 receiver enable-notifications register (write 0x00 0x09 0x00 to enable wireless + software-present notifications).</summary>
    public const byte RegisterEnableHidPpNotifications = 0x00;

    /// <summary>Pairing register — write 0x02 0x02 0x00 to enumerate paired devices.</summary>
    public const byte RegisterConnectionState = 0x02;

    /// <summary>
    /// Builds the HID++ 1.0 short frame that enables receiver notifications
    /// (wireless events + software-present flag). Matches Solaar's
    /// <c>enable_notifications</c> + CleverSwitch's <c>ENABLE_HIDPP_NOTIFICATIONS</c>.
    /// Wire: <c>10 FF 80 00 00 09 00</c>.
    /// </summary>
    public static HidPpFrame BuildEnableNotificationsFrame() =>
        HidPpFrame.Hidpp10Short(
            deviceIndex: HidPpConstants.DeviceIndexReceiver,
            subId: SubIdSetRegister,
            parameters: [RegisterEnableHidPpNotifications, 0x00, 0x09, 0x00]);

    /// <summary>
    /// Builds the HID++ 1.0 short frame that asks the receiver to (re-)emit
    /// connection notifications for every paired device. Wire:
    /// <c>10 FF 80 02 02 00 00</c>.
    /// </summary>
    public static HidPpFrame BuildEnumerateDevicesFrame() =>
        HidPpFrame.Hidpp10Short(
            deviceIndex: HidPpConstants.DeviceIndexReceiver,
            subId: SubIdSetRegister,
            parameters: [RegisterConnectionState, 0x02, 0x00, 0x00]);
}
