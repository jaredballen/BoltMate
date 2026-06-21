using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.Hid.IOKit;

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

    /// <summary>
    /// Diagnostic logger. Defaults to NullLogger — the App layer sets this
    /// to a real Serilog-backed ILogger at startup so every IOHIDCheckAccess
    /// and IOHIDRequestAccess call is captured in the on-disk log.
    /// </summary>
    public static ILogger Log { get; set; } = NullLogger.Instance;

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
            var raw = IOHIDCheckAccess(RequestTypeListenEvent);
            var status = (Status)raw;
            Log.LogDebug("Check: IOHIDCheckAccess returned {Raw} → {Status}", raw, status);
            return status;
        }
        catch (DllNotFoundException ex)
        {
            Log.LogWarning(ex, "Check: IOKit.framework not loadable → NotApplicable");
            return Status.NotApplicable;
        }
        catch (EntryPointNotFoundException ex)
        {
            Log.LogWarning(ex, "Check: IOHIDCheckAccess entry point missing → NotApplicable");
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
            var ok = IOHIDRequestAccess(RequestTypeListenEvent);
            Log.LogInformation("Request: IOHIDRequestAccess returned {Result}", ok);
            return ok;
        }
        catch (DllNotFoundException ex)
        {
            Log.LogWarning(ex, "Request: IOKit.framework not loadable → false");
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            Log.LogWarning(ex, "Request: IOHIDRequestAccess entry point missing → false");
            return false;
        }
    }

    /// <summary>
    /// Opens the macOS Privacy &amp; Security → Input Monitoring pane so the
    /// user can re-enable BoltMate after a previous denial. No-op on
    /// non-macOS platforms.
    /// </summary>
    public static void OpenSystemSettings()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;
        try
        {
            Process.Start("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent");
        }
        catch
        {
            // best-effort
        }
    }
}
