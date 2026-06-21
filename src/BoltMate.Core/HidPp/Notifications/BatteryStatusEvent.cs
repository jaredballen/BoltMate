using BoltMate.Core.HidPp.Features;

namespace BoltMate.Core.HidPp.Notifications;

/// <summary>
/// HID++ 2.0 <c>batteryStatusEvent</c> — function 0x0, swid 0 on feature
/// 0x1004 (UNIFIED_BATTERY). Devices push this when their charge controller
/// state changes — cable plug/unplug, level crosses a Logitech-firmware
/// threshold, charging complete, etc. Letting us listen for these means we
/// never have to poll: the device tells us when something happens.
/// </summary>
public readonly record struct BatteryStatusEvent(
    byte DeviceIndex,
    BatteryStatus Status)
{
    /// <summary>
    /// Attempts to parse <paramref name="frame"/> as a batteryStatusEvent on
    /// the given <paramref name="unifiedBatteryFeatureIndex"/>. Returns false
    /// for frames that don't match the shape — wrong report id, wrong
    /// feature index, wrong function or swid.
    /// </summary>
    public static bool TryParse(HidPpFrame frame, byte unifiedBatteryFeatureIndex, out BatteryStatusEvent notification)
    {
        notification = default;

        // Battery events arrive as long-report notifications (0x11). Short
        // frames on this feature are never push events.
        if (!frame.IsLong) return false;
        if (frame.FeatureIndex != unifiedBatteryFeatureIndex) return false;
        // Function 0x0, swid 0 — the canonical "notification" pattern.
        if (frame.Function != 0 || frame.SwId != 0) return false;

        var status = BatteryService.ParsePayload(frame.Parameters.Span);
        if (status is null) return false;

        notification = new BatteryStatusEvent(frame.DeviceIndex, status.Value);
        return true;
    }
}
