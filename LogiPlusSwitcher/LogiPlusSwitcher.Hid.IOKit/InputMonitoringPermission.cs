using System.Runtime.InteropServices;

namespace LogiPlusSwitcher.Hid.IOKit;

/// <summary>
/// Checks the macOS Input Monitoring permission required for HID device
/// access via IOKit (and therefore for libhidapi reads/writes). No-op on
/// non-macOS platforms (returns Granted).
/// </summary>
/// <remarks>
/// Wraps <c>IOHIDCheckAccess(kIOHIDRequestTypeListenEvent)</c> from
/// IOKit.framework. The first call from an unprivileged process triggers
/// the system prompt — calling it at startup is the polite thing to do.
/// </remarks>
public static class InputMonitoringPermission
{
    // kIOHIDRequestType (Apple's enum):
    //   kIOHIDRequestTypeListenEvent = 1   <-- the one HID readers need
    private const uint RequestTypeListenEvent = 1;

    // IOHIDAccessType:
    //   kIOHIDAccessTypeGranted  = 0
    //   kIOHIDAccessTypeDenied   = 1
    //   kIOHIDAccessTypeUnknown  = 2
    public enum Status
    {
        Granted = 0,
        Denied = 1,
        Unknown = 2,
        NotApplicable = 99,
    }

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit",
        EntryPoint = "IOHIDCheckAccess", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint IOHIDCheckAccess(uint requestType);

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit",
        EntryPoint = "IOHIDRequestAccess", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool IOHIDRequestAccess(uint requestType);

    /// <summary>
    /// Synchronously checks the current Input Monitoring permission.
    /// Returns <see cref="Status.NotApplicable"/> on non-macOS hosts.
    /// </summary>
    public static Status Check()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Status.NotApplicable;
        try
        {
            var result = IOHIDCheckAccess(RequestTypeListenEvent);
            return (Status)result;
        }
        catch (DllNotFoundException)
        {
            return Status.NotApplicable;
        }
        catch (EntryPointNotFoundException)
        {
            return Status.NotApplicable;
        }
    }

    /// <summary>
    /// Asks the OS for Input Monitoring permission. Triggers the system
    /// prompt on first call from an unprivileged process. Returns true if
    /// permission was granted (immediately or after the prompt).
    /// </summary>
    public static bool Request()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return true;
        try
        {
            return IOHIDRequestAccess(RequestTypeListenEvent);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
