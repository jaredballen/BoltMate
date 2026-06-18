using HidApi;
using LogiPlusSwitcher.Core.Bolt;

namespace LogiPlusSwitcher.Core.Hid;

/// <summary>
/// libhidapi-backed transport. Wires up the macOS non-exclusive flag at
/// construction time so subsequent opens coexist with Logi Options+.
/// </summary>
public sealed class HidApiReceiverTransport : IReceiverTransport
{
    public HidApiReceiverTransport()
    {
        HidApiBridge.EnsureNativeLibraryResolver();
        // Best-effort on macOS; no-op (returns false) on Windows/Linux.
        HidApiBridge.SetMacOsNonExclusive();
    }

    public IReadOnlyList<BoltReceiverInfo> Enumerate()
    {
        var infos = HidApi.Hid.Enumerate(BoltConstants.LogitechVendorId, BoltConstants.BoltReceiverProductId);
        return BoltReceiverInfo.Filter(infos).ToList();
    }

    public IReceiverConnection Open(BoltReceiverInfo info)
    {
        var device = new Device(info.Path);
        return new HidApiReceiverConnection(device);
    }
}
