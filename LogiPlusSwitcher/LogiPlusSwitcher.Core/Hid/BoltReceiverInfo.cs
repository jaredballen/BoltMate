using HidApi;
using LogiPlusSwitcher.Core.Bolt;

namespace LogiPlusSwitcher.Core.Hid;

/// <summary>
/// Identifies a single Logitech Bolt receiver's HID++ management interface
/// (the only one of its sibling HID interfaces that carries HID++ traffic).
/// </summary>
public sealed record BoltReceiverInfo(
    string Path,
    string Serial,
    string ProductString,
    string ManufacturerString,
    ushort ReleaseNumber)
{
    /// <summary>
    /// Filters a <see cref="DeviceInfo"/> stream to the HID++ management interface
    /// of every Bolt receiver attached. Bolt receivers expose multiple HID interfaces
    /// (keyboard, mouse, vendor-defined); only the vendor interface with
    /// <c>UsagePage=0xFF00, Usage=0x0001</c> carries HID++ traffic.
    /// </summary>
    public static IEnumerable<BoltReceiverInfo> Filter(IEnumerable<DeviceInfo> infos)
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
