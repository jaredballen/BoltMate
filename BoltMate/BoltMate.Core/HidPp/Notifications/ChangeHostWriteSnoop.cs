using BoltMate.Core.HidPp.Features;

namespace BoltMate.Core.HidPp.Notifications;

/// <summary>
/// Recognises HID++ 2.0 <c>SetCurrentHost</c> writes (feature 0x1814 fn=1)
/// that someone OTHER than us issued. The Bolt receiver echoes outbound
/// HID++ 2.0 writes back on its management interface to every observer;
/// when Logi Options+ executes a Mouse Flow handover it writes
/// <c>0x1814 setCurrentHost</c> to the mouse, and we see it on the wire.
/// That is the signal we use to detect Flow events so we can fan the
/// switch out to the rest of the paired devices.
/// </summary>
public readonly record struct ChangeHostWriteSnoop(
    byte DeviceIndex,
    byte FeatureIndex,
    byte TargetHost,
    byte SwId)
{
    /// <summary>
    /// Attempts to parse <paramref name="frame"/> as a SetCurrentHost write.
    /// Matches by function index <c>1</c> and the device-specific
    /// <paramref name="changeHostFeatureIndex"/> for that slot. Caller must
    /// filter by <paramref name="ourSwId"/> if it wants to skip our own writes.
    /// </summary>
    public static bool TryParse(HidPpFrame frame, byte changeHostFeatureIndex, byte ourSwId, out ChangeHostWriteSnoop snoop)
    {
        snoop = default;

        if (!frame.IsLong)
            return false;
        if (frame.FeatureIndex != changeHostFeatureIndex)
            return false;
        if (frame.Function != 1)
            return false;
        if (frame.SwId == ourSwId)
            return false; // our own write
        if (frame.SwId == 0)
            return false; // device notification, not a host write

        snoop = new ChangeHostWriteSnoop(
            DeviceIndex: frame.DeviceIndex,
            FeatureIndex: frame.FeatureIndex,
            TargetHost: frame.Parameters.Span[0],
            SwId: (byte)frame.SwId);
        return true;
    }
}
