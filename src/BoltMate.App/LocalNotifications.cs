using System;
using System.Runtime.InteropServices;

namespace BoltMate.App;

/// <summary>
/// Best-effort local notification surface. Used to nudge the user about
/// missing OS permissions on subsequent launches. Fires AT MOST ONCE per
/// app session — caller responsibility (we don't dedupe at this layer);
/// see <c>App.axaml.cs</c>.
/// </summary>
/// <remarks>
/// Per-platform behavior:
///   • macOS: NSUserNotification via Objective-C runtime P/Invoke. Legacy
///     API (deprecated since 10.14) but works WITHOUT entitlements, an
///     Info.plist NSUserNotificationAlertStyle, or app-store registration.
///     Modern UNUserNotificationCenter would need a bundled .app + plist
///     setup we're not ready for.
///   • Windows: tray-only. Modern toast notifications via
///     CommunityToolkit.WinUI.Notifications need a Windows-target TFM
///     (net10.0-windows10.0.xxxxx) which the App project doesn't carry,
///     plus an AppUserModelID and start-menu shortcut registration to
///     avoid the "this app isn't registered" toast quirk. Out of scope
///     for this pass — the tray-icon yellow "!" badge plus the "Fix
///     permissions…" menu item carry the signal on Windows.
///   • Linux: skip entirely.
/// </remarks>
public static class LocalNotifications
{
    /// <summary>
    /// Show a notification. Returns true if it was posted, false if the
    /// platform has no implementation or the call failed.
    /// </summary>
    /// <param name="title">Title line.</param>
    /// <param name="body">Body line.</param>
    public static bool TryPost(string title, string body)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                return PostMac(title, body);
            // Windows + Linux: tray-only fallback (see remarks above).
            return false;
        }
        catch
        {
            return false;
        }
    }

    // ====================================================================
    // macOS — NSUserNotification via Objective-C runtime
    // ====================================================================

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr ObjCGetClass([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr SelRegisterName([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendMessage_str(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPStr)] string s);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendMessage_void_obj(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendMessage_alloc(IntPtr receiver, IntPtr selector);

    private static bool PostMac(string title, string body)
    {
        try
        {
            // Build NSString objects.
            var nsStringClass = ObjCGetClass("NSString");
            var stringWithUtf8 = SelRegisterName("stringWithUTF8String:");
            var titleNs = SendMessage_str(nsStringClass, stringWithUtf8, title);
            var bodyNs  = SendMessage_str(nsStringClass, stringWithUtf8, body);

            // [[NSUserNotification alloc] init]
            var nunClass = ObjCGetClass("NSUserNotification");
            if (nunClass == IntPtr.Zero) return false;
            var notif = SendMessage_alloc(nunClass, SelRegisterName("alloc"));
            notif = SendMessage(notif, SelRegisterName("init"));
            if (notif == IntPtr.Zero) return false;

            // [notif setTitle:titleNs] / setInformativeText:bodyNs
            SendMessage_void_obj(notif, SelRegisterName("setTitle:"), titleNs);
            SendMessage_void_obj(notif, SelRegisterName("setInformativeText:"), bodyNs);

            // [[NSUserNotificationCenter defaultUserNotificationCenter] deliverNotification:notif]
            var centerClass = ObjCGetClass("NSUserNotificationCenter");
            if (centerClass == IntPtr.Zero) return false;
            var center = SendMessage(centerClass, SelRegisterName("defaultUserNotificationCenter"));
            if (center == IntPtr.Zero) return false;
            SendMessage_void_obj(center, SelRegisterName("deliverNotification:"), notif);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
