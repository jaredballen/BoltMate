using System.Buffers.Binary;
using LogiPlusSwitcher.Core.HidPp.Features;

namespace LogiPlusSwitcher.Core.HidPp.Notifications;

/// <summary>
/// HID++ 2.0 <c>divertedButtonsEvent</c> — fn=0, swid=0 on feature 0x1B04.
/// Emitted by a device when a CID that we have diverted via
/// <see cref="ReprogControlsService.SetCidReportingAsync"/> is pressed.
/// The event carries up to four pressed CIDs simultaneously; release of all
/// diverted buttons is signalled by a frame with all-zero CID slots.
/// </summary>
public readonly record struct DivertedButtonsNotification(
    byte DeviceIndex,
    ushort? Cid1,
    ushort? Cid2,
    ushort? Cid3,
    ushort? Cid4)
{
    /// <summary>The first non-zero CID, or null if this was a release event.</summary>
    public ushort? PrimaryCid => Cid1 ?? Cid2 ?? Cid3 ?? Cid4;

    /// <summary>True if this is a "all buttons released" event.</summary>
    public bool IsReleaseEvent => Cid1 is null && Cid2 is null && Cid3 is null && Cid4 is null;

    /// <summary>True if any of the pressed CIDs is one of D1/D2/D3.</summary>
    public bool ContainsHostSwitch =>
        (Cid1 is { } c1 && EasySwitchCids.IsHostSwitch(c1)) ||
        (Cid2 is { } c2 && EasySwitchCids.IsHostSwitch(c2)) ||
        (Cid3 is { } c3 && EasySwitchCids.IsHostSwitch(c3)) ||
        (Cid4 is { } c4 && EasySwitchCids.IsHostSwitch(c4));

    /// <summary>
    /// Returns the zero-indexed target host (0..2) if a host-switch CID is in
    /// the press, otherwise null.
    /// </summary>
    public int? TargetHost
    {
        get
        {
            foreach (var cid in new[] { Cid1, Cid2, Cid3, Cid4 })
            {
                if (cid is { } c && EasySwitchCids.ToHostIndex(c) is { } host)
                    return host;
            }
            return null;
        }
    }

    /// <summary>
    /// Attempts to parse <paramref name="frame"/> as a divertedButtonsEvent on
    /// the given <paramref name="reprogControlsFeatureIndex"/> (the device-
    /// specific feature index for 0x1B04). Returns false for frames that
    /// don't match the shape (wrong report id, wrong feature index, swid not 0).
    /// </summary>
    public static bool TryParse(HidPpFrame frame, byte reprogControlsFeatureIndex, out DivertedButtonsNotification notification)
    {
        notification = default;

        if (!frame.IsLong)
            return false;
        if (frame.FeatureIndex != reprogControlsFeatureIndex)
            return false;
        if (frame.Function != 0 || frame.SwId != 0)
            return false;

        var p = frame.Parameters.Span;
        if (p.Length < 8)
            return false;

        notification = new DivertedButtonsNotification(
            DeviceIndex: frame.DeviceIndex,
            Cid1: ReadCid(p, 0),
            Cid2: ReadCid(p, 2),
            Cid3: ReadCid(p, 4),
            Cid4: ReadCid(p, 6));
        return true;
    }

    private static ushort? ReadCid(ReadOnlySpan<byte> span, int offset)
    {
        var cid = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset, 2));
        return cid == 0 ? null : cid;
    }
}
