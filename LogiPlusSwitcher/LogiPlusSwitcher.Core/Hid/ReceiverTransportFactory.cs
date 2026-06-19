using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LogiPlusSwitcher.Core.Hid;

/// <summary>
/// Picks the right HID transport for the current platform.
/// macOS gets the IOKit-direct transport (libhidapi's hid_open_path doesn't
/// honour shared access on recent macOS and breaks device firmware buttons —
/// see <c>project_mac_hid_open_breaks_device</c> memory). Windows and Linux
/// continue to use the libhidapi-backed transport.
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
