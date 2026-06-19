using LogiPlusSwitcher.Hid.Abstractions;

namespace LogiPlusSwitcher.Hid.HidApi;

/// <summary>
/// libhidapi-specific conversion helpers — filters a raw
/// <see cref="global::HidApi.DeviceInfo"/> enumeration down to Bolt receiver
/// management interfaces and projects each one to a backend-agnostic
/// <see cref="BoltReceiverInfo"/>.
/// </summary>
internal static class HidApiDeviceInfoExtensions
{
    /// <summary>
    /// Filters a <see cref="global::HidApi.DeviceInfo"/> stream to the HID++
    /// management interface of every Bolt receiver attached. Bolt receivers
    /// expose multiple HID interfaces (keyboard, mouse, vendor-defined); only
    /// the vendor interface with <c>UsagePage=0xFF00, Usage=0x0001</c> carries
    /// HID++ traffic.
    /// </summary>
    public static IEnumerable<BoltReceiverInfo> ToBoltReceiverInfos(IEnumerable<global::HidApi.DeviceInfo> infos)
    {
        foreach (var info in infos)
        {
            if (info.VendorId != BoltConstants.LogitechVendorId)
                continue;
            if (info.ProductId != BoltConstants.BoltReceiverProductId)
                continue;
            if (info.UsagePage != BoltConstants.ManagementUsagePage)
                continue;
            if (info.Usage != BoltConstants.ManagementUsage)
                continue;

            yield return new BoltReceiverInfo(
                info.Path,
                info.SerialNumber,
                info.ProductString,
                info.ManufacturerString,
                info.ReleaseNumber);
        }
    }
}
