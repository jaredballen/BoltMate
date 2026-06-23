using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.App.Services;

/// <summary>
/// macOS notification surface. <b>Currently delivers via the legacy
/// NSUserNotification path</b> — the OS still routes them properly now
/// that the bundle is signed with com.jaredballen.BoltMate (see the
/// ad-hoc resign step in Directory.Build.targets), so banners fire on
/// modern macOS without needing UNUserNotificationCenter for delivery.
/// </summary>
/// <remarks>
/// UN center integration is deferred. Two attempts to hand-roll the
/// Objective-C block ABI in pure C# segfaulted on init — the second one
/// with a "Data Abort byte write Permission fault" at address 0 during
/// what looked like the OS's Block_copy step. The most likely fix is to
/// declare blocks with <c>_NSConcreteMallocBlock</c> + a proper refcount
/// in the flags field (rather than <c>_NSConcreteStackBlock</c>), but
/// that needs careful testing in isolation. Until then,
/// <see cref="GetAuthorizationStatus"/> returns <c>NotDetermined</c> as
/// a placeholder so callers fall back to the locally-persisted
/// <c>AppSettings.NotificationsState</c> as the source of truth.
///
/// Delivery uses NSUserNotification: works for unsigned/sideloaded apps
/// after the bundle's signing identity is consistent with its
/// CFBundleIdentifier, which we have now. The first notification fired
/// after a clean install triggers the OS's reactive "Allow / Don't Allow"
/// banner; the app's row appears in System Settings → Notifications
/// after that.
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

    /// <summary>
    /// Stubbed until the UN bridge lands — returns NotDetermined so the
    /// caller falls back to the persisted user preference.
    /// </summary>
    public static AuthorizationStatus GetAuthorizationStatus() => AuthorizationStatus.NotDetermined;

    public static Task<AuthorizationStatus> GetAuthorizationStatusAsync()
        => Task.FromResult(AuthorizationStatus.NotDetermined);

    /// <summary>
    /// Stubbed until the UN bridge lands — opens System Settings since
    /// we have no API to drive the prompt without UN.
    /// </summary>
    public static Task<bool> RequestAuthorizationAsync(
        AuthorizationOptions options = AuthorizationOptions.Alert | AuthorizationOptions.Sound,
        CancellationToken ct = default)
    {
        NotificationsSettings.OpenOsSettings();
        return Task.FromResult(true);
    }

    /// <summary>
    /// NSUserNotification delivery. Works on the now-correctly-signed
    /// bundle. Returns true if the deliverNotification: send completed
    /// without throwing — not a guarantee the banner rendered, the OS
    /// can still drop it under Focus / Do Not Disturb.
    /// </summary>
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
