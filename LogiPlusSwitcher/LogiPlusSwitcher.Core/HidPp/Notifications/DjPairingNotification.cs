namespace LogiPlusSwitcher.Core.HidPp.Notifications;

/// <summary>
/// HID++ 1.0 sub-id <c>0x41</c> (DJ_PAIRING) — emitted by the receiver when a
/// paired device's wireless link changes state. Carries the receiver slot
/// (<see cref="DeviceIndex"/>), link direction (<see cref="LinkLost"/>), and
/// the device's WPID. The Bolt protocol uses address byte <c>0x10</c>
/// ("encrypted link") for these notifications.
/// </summary>
/// <remarks>
/// Decoded per Solaar (<c>notifications.py:176-220</c>, <c>common.py:713-720</c>).
/// </remarks>
public readonly record struct DjPairingNotification(
    byte DeviceIndex,
    byte Address,
    byte Flags,
    ushort Wpid)
{
    /// <summary>Receiver slot was unpaired (sub-id 0x40 address 0x02). Not parsed here.</summary>
    public const byte AddressUnpaired = 0x02;

    /// <summary>Bolt-protocol encrypted link.</summary>
    public const byte AddressBolt = 0x10;

    /// <summary>Link-lost flag in the first data byte.</summary>
    public const byte FlagLinkLost = 0x40;

    /// <summary>Encrypted-link flag in the first data byte.</summary>
    public const byte FlagEncrypted = 0x20;

    /// <summary>Software-present flag in the first data byte.</summary>
    public const byte FlagSoftwarePresent = 0x10;

    /// <summary>True if the wireless link to this slot just dropped.</summary>
    public bool LinkLost => (Flags & FlagLinkLost) != 0;

    /// <summary>True if the wireless link to this slot just came up.</summary>
    public bool LinkEstablished => !LinkLost;

    /// <summary>True for the Bolt protocol; false for older Unifying receivers.</summary>
    public bool IsBolt => Address == AddressBolt;

    /// <summary>
    /// Returns true and populates <paramref name="notification"/> if
    /// <paramref name="frame"/> is a DJ_PAIRING notification on a paired slot.
    /// </summary>
    public static bool TryParse(HidPpFrame frame, out DjPairingNotification notification)
    {
        notification = default;

        if (!frame.IsShort)
            return false;
        if (frame.FeatureIndex != 0x41)
            return false;
        if (frame.DeviceIndex is < HidPpConstants.DeviceIndexFirstSlot or > HidPpConstants.DeviceIndexLastSlot)
            return false;

        // For HID++ 1.0 frames, the FunctionAndSwId field is actually the
        // first data byte (Solaar treats it as `address`), and parameter bytes
        // hold the flags + WPID.
        var address = frame.FunctionAndSwId;
        var p = frame.Parameters.Span;
        if (p.Length < 3)
            return false;

        var flags = p[0];
        var wpid = (ushort)(p[1] | (p[2] << 8)); // little-endian

        notification = new DjPairingNotification(frame.DeviceIndex, address, flags, wpid);
        return true;
    }
}
