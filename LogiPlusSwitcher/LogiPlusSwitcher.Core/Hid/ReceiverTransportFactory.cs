using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LogiPlusSwitcher.Core.Hid;

/// <summary>
/// Picks the right HID transport for the current platform. macOS gets the
/// IOKit-direct transport because libhidapi 0.15.0 silently ignores
/// <c>hid_darwin_set_open_exclusive(0)</c> on macOS Sequoia — the flag is
/// set, the documented call order is followed, but device opens still seize.
/// Definitive proof: <c>diag-libhidapi-shared</c> CLI command on this code
/// base shows a second open in the same process fails with
/// <c>kIOReturnExclusiveAccess</c> while the first is held. Holding the
/// management interface open via libhidapi also disables device firmware
/// button handling (wheel-mode toggle, gesture buttons). IOKit-direct with
/// explicit <c>kIOHIDOptionsTypeNone</c> fixes both.
/// Windows and Linux stay on libhidapi (no equivalent bug observed there).
/// </summary>
public static class ReceiverTransportFactory
{
    public static IReceiverTransport Create(ILoggerFactory? loggerFactory = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new IOKitReceiverTransport(loggerFactory);
        return new HidApiReceiverTransport(loggerFactory);
    }
}
