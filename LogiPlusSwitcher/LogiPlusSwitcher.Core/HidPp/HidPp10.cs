namespace LogiPlusSwitcher.Core.HidPp;

/// <summary>
/// HID++ 1.0 receiver-level register helpers. The Bolt receiver still speaks
/// HID++ 1.0 for housekeeping commands (enable notifications, enumerate paired
/// devices, pairing-register operations, unpair) — all addressed to
/// <c>device_index 0xFF</c>.
/// </summary>
/// <remarks>
/// Register addresses in Solaar use the convention <c>0x0NN</c> for
/// short-register operations (sub-id 0x80 / 0x81) and <c>0x2NN</c> for
/// long-register operations (sub-id 0x82 / 0x83). The low byte is always the
/// actual register number on the wire.
/// </remarks>
public static class HidPp10
{
    /// <summary>Sub-id 0x80 SET_REGISTER (short request from host to receiver).</summary>
    public const byte SubIdSetRegister = 0x80;

    /// <summary>Sub-id 0x81 GET_REGISTER (short request).</summary>
    public const byte SubIdGetRegister = 0x81;

    /// <summary>Sub-id 0x82 SET_LONG_REGISTER (long request for registers prefixed 0x2NN in Solaar).</summary>
    public const byte SubIdSetLongRegister = 0x82;

    /// <summary>Sub-id 0x83 GET_LONG_REGISTER.</summary>
    public const byte SubIdGetLongRegister = 0x83;

    /// <summary>Short receiver enable-notifications register.</summary>
    public const byte RegisterEnableHidPpNotifications = 0x00;

    /// <summary>Short pairing/connection-state register — write 0x02 0x02 0x00 to enumerate paired devices.</summary>
    public const byte RegisterConnectionState = 0x02;

    /// <summary>Long receiver info register (Solaar's <c>RECEIVER_INFO = 0x2B5</c>).</summary>
    public const byte RegisterReceiverInfo = 0xB5;

    /// <summary>Short Bolt device discovery register (Solaar's <c>BOLT_DEVICE_DISCOVERY = 0xC0</c>).</summary>
    public const byte RegisterBoltDeviceDiscovery = 0xC0;

    /// <summary>Long Bolt pairing register (Solaar's <c>BOLT_PAIRING = 0x2C1</c>). Sub-action 0x03 unpairs a slot.</summary>
    public const byte RegisterBoltPairing = 0xC1;

    /// <summary>Long Bolt unique receiver id register (Solaar's <c>BOLT_UNIQUE_ID = 0x2FB</c>).</summary>
    public const byte RegisterBoltUniqueId = 0xFB;

    /// <summary>Sub-action inside BOLT_PAIRING for unpair.</summary>
    public const byte BoltPairingSubActionUnpair = 0x03;

    /// <summary>RECEIVER_INFO sub-register: per-slot BOLT_PAIRING_INFORMATION starts at <c>0x50 + slot</c>.</summary>
    public const byte InfoSubRegisterBoltPairingInfoBase = 0x50;

    /// <summary>RECEIVER_INFO sub-register: per-slot BOLT_DEVICE_NAME starts at <c>0x60 + slot</c>.</summary>
    public const byte InfoSubRegisterBoltDeviceNameBase = 0x60;

    /// <summary>RECEIVER_INFO sub-register: receiver firmware / hardware information.</summary>
    public const byte InfoSubRegisterReceiverInformation = 0x03;

    /// <summary>
    /// Builds the HID++ 1.0 short frame that enables receiver notifications.
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

    /// <summary>
    /// Builds the Bolt unpair frame for a single slot. Long-register SET to
    /// BOLT_PAIRING with sub-action 0x03 and the target slot index. Wire:
    /// <c>11 FF 82 C1 03 &lt;slot&gt; 00 00 00 00 00 00 00 00 00 00 00 00 00 00</c>.
    /// </summary>
    /// <param name="slot">Receiver slot to unpair (1..6).</param>
    public static HidPpFrame BuildBoltUnpairFrame(byte slot)
    {
        if (slot is < HidPpConstants.DeviceIndexFirstSlot or > HidPpConstants.DeviceIndexLastSlot)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Bolt receiver slot must be 1..6.");

        Span<byte> parameters = stackalloc byte[HidPpConstants.LongParameterLength];
        parameters[0] = RegisterBoltPairing;
        parameters[1] = BoltPairingSubActionUnpair;
        parameters[2] = slot;
        // remaining bytes stay zero

        return HidPpFrame.Hidpp10Long(
            deviceIndex: HidPpConstants.DeviceIndexReceiver,
            subId: SubIdSetLongRegister,
            parameters: parameters);
    }

    /// <summary>
    /// Builds a short GET_LONG_REGISTER read request for RECEIVER_INFO with a
    /// specific sub-register. Used for Bolt per-slot metadata reads
    /// (<c>BOLT_PAIRING_INFORMATION = 0x50 + slot</c>, <c>BOLT_DEVICE_NAME = 0x60 + slot</c>).
    /// </summary>
    /// <param name="subRegister">Sub-register byte (e.g. <c>0x53</c> for slot-3 pairing info).</param>
    /// <param name="extraByte">Optional extra byte for sub-register addressing (Solaar uses <c>0x01</c> for device-name reads).</param>
    public static HidPpFrame BuildReadReceiverInfoFrame(byte subRegister, byte extraByte = 0x00) =>
        HidPpFrame.Hidpp10Short(
            deviceIndex: HidPpConstants.DeviceIndexReceiver,
            subId: SubIdGetLongRegister,
            parameters: [RegisterReceiverInfo, subRegister, extraByte, 0x00]);

    /// <summary>
    /// Builds the read request for BOLT_UNIQUE_ID (the receiver's own serial /
    /// identifier). Long-register read on 0xFB.
    /// </summary>
    public static HidPpFrame BuildReadBoltUniqueIdFrame() =>
        HidPpFrame.Hidpp10Short(
            deviceIndex: HidPpConstants.DeviceIndexReceiver,
            subId: SubIdGetLongRegister,
            parameters: [RegisterBoltUniqueId, 0x00, 0x00, 0x00]);
}
