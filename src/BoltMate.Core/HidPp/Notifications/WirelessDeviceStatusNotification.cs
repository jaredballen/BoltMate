namespace BoltMate.Core.HidPp.Notifications;

/// <summary>
/// HID++ 2.0 device-initiated notification on feature 0x1D4B
/// (WIRELESS_DEVICE_STATUS). Fires when the device firmware has finished
/// post-link-up initialisation and is ready to answer host-side reads /
/// accept config writes. DJ 0x41 link-up fires earlier — chunked reads
/// (0x1815 host friendly names, 0x0005 device name) issued between 0x41
/// and this notification often come back with cached defaults.
/// </summary>
/// <remarks>
/// Wire shape (long-report, swid 0, function nibble 0):
/// <list type="bullet">
/// <item><c>data[0]</c> — reconnection type (Solaar ignores).</item>
/// <item><c>data[1]</c> — set to 1 when the device is requesting that the
///       host re-apply its software config. Treat as "device fully ready".</item>
/// <item><c>data[2]</c> — reason. 1 = "powered on" (cold boot) vs wake.</item>
/// </list>
/// The notification can fire repeatedly during a session, so consumers
/// must dedup per-link if they only want a one-shot trigger.
/// </remarks>
public readonly record struct WirelessDeviceStatusNotification(
    byte DeviceIndex,
    bool ReconfigRequested,
    bool PoweredOn)
{
    public static bool TryParse(HidPpFrame frame, byte wirelessDeviceStatusFeatureIndex, out WirelessDeviceStatusNotification notification)
    {
        notification = default;

        if (!frame.IsLong) return false;
        if (frame.FeatureIndex != wirelessDeviceStatusFeatureIndex) return false;
        if (frame.Function != 0 || frame.SwId != 0) return false;

        var p = frame.Parameters.Span;
        if (p.Length < 3) return false;

        notification = new WirelessDeviceStatusNotification(
            DeviceIndex: frame.DeviceIndex,
            ReconfigRequested: p[1] == 1,
            PoweredOn: p[2] == 1);
        return true;
    }
}
