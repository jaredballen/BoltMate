using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.App.Services;

/// <summary>
/// macOS notification surface. Currently a thin wrapper over the legacy
/// NSUserNotification path that lands properly on the now-correctly-
/// signed bundle (com.jaredballen.BoltMate identifier bound to plist —
/// see Directory.Build.targets). UNUserNotificationCenter integration
/// is deferred: hand-rolling the Objective-C block ABI for the
/// completion handlers is fragile and crashed at startup on our first
/// attempt. The Settings-managed permission state path covers the user-
/// visible state-tracking requirement without needing UN.
/// </summary>
/// <remarks>
/// What we lose by NOT switching to UN center:
///   • No programmatic <c>requestAuthorization</c> — first-time users
///     are introduced to the notification by it firing, exactly like the
///     pre-existing NSUserNotification behaviour. The Settings card +
///     welcome page both expose the OS settings pane via the
///     "Open notification settings" deep-link so users can find the
///     real toggle when they want to.
///   • No accurate auth-status probe. We return Authorized as a
///     placeholder; the platform-specific permission impl in
///     PermissionsService will still query state via a future hook —
///     for now its toggle reflects "always on" on Mac. We'll wire the
///     real getNotificationSettings call once we have a tested
///     completion-handler block helper.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal static class MacUserNotifications
{
    public enum AuthorizationStatus
    {
        NotDetermined = 0,
        Denied        = 1,
        Authorized    = 2,
        Provisional   = 3,
        Ephemeral     = 4,
    }

    [Flags]
    public enum AuthorizationOptions : ulong
    {
        Badge = 1,
        Sound = 2,
        Alert = 4,
    }

    public static ILogger Log { get; set; } = NullLogger.Instance;

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr GetSelector(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr sel);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send_str(IntPtr receiver, IntPtr sel, [MarshalAs(UnmanagedType.LPStr)] string s);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send_obj(IntPtr receiver, IntPtr sel, IntPtr a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send_obj_obj(IntPtr receiver, IntPtr sel, IntPtr a1, IntPtr a2);

    private static IntPtr NSString(string s)
    {
        var nsString = GetClass("NSString");
        return Send_str(nsString, GetSelector("stringWithUTF8String:"), s);
    }

    public static AuthorizationStatus GetAuthorizationStatus()
    {
        // Stub: until we have a tested completion-handler bridge we report
        // Authorized so the Settings toggle defaults to on (matching the
        // OS default for unsigned/sideloaded apps that haven't been
        // explicitly muted). Real state-probe lands when UN integration
        // does — issue tracked in commit message.
        return AuthorizationStatus.Authorized;
    }

    public static Task<bool> RequestAuthorizationAsync(
        AuthorizationOptions options = AuthorizationOptions.Alert | AuthorizationOptions.Sound,
        CancellationToken ct = default)
    {
        // No-op for now — without UN center we have no API to drive the
        // request. The welcome-page Allow button on Mac therefore just
        // opens System Settings; the OS prompt fires the first time we
        // actually post a notification through the legacy path.
        NotificationsSettings.OpenOsSettings();
        return Task.FromResult(true);
    }

    public static bool Deliver(string title, string body)
    {
        try
        {
            var nsStringClass = GetClass("NSString");
            var stringWithUtf8 = GetSelector("stringWithUTF8String:");
            var titleNs = Send_str(nsStringClass, stringWithUtf8, title);
            var bodyNs  = Send_str(nsStringClass, stringWithUtf8, body);

            var nunClass = GetClass("NSUserNotification");
            if (nunClass == IntPtr.Zero) return false;
            var notif = Send(nunClass, GetSelector("alloc"));
            notif = Send(notif, GetSelector("init"));
            if (notif == IntPtr.Zero) return false;

            Send_obj(notif, GetSelector("setTitle:"), titleNs);
            Send_obj(notif, GetSelector("setInformativeText:"), bodyNs);

            var centerClass = GetClass("NSUserNotificationCenter");
            if (centerClass == IntPtr.Zero) return false;
            var center = Send(centerClass, GetSelector("defaultUserNotificationCenter"));
            if (center == IntPtr.Zero) return false;
            Send_obj(center, GetSelector("deliverNotification:"), notif);
            return true;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "NSUserNotification deliver threw");
            return false;
        }
    }
}
