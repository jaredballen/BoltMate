using System;
using BoltMate.App.Services;

namespace BoltMate.App;

/// <summary>
/// Best-effort local notification surface. Used to nudge the user about
/// missing OS permissions on subsequent launches. Auth-gated by the OS —
/// if the user has denied notifications the post drops silently.
/// </summary>
/// <remarks>
/// Per-platform:
///   • macOS: UNUserNotificationCenter via the
///     <see cref="MacUserNotifications"/> ObjC bridge. Replaces the
///     deprecated NSUserNotification path that landed silently in
///     Notification Center even after explicit user grant.
///   • Windows: PowerShell-driven WinRT projection via
///     <see cref="WinToast"/>. Falls back to no-op if the user has
///     toggled notifications off in Settings → System → Notifications →
///     BoltMate.
///   • Linux: skip entirely.
/// </remarks>
public static class LocalNotifications
{
    /// <summary>
    /// Show a notification. Returns true if the call was dispatched —
    /// not a guarantee the banner actually rendered (auth status,
    /// Focus / Do Not Disturb, etc. can intercept downstream).
    /// </summary>
    public static bool TryPost(string title, string body)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                return MacUserNotifications.Deliver(title, body);
            if (OperatingSystem.IsWindows())
                return WinToast.TryPost(title, body);
            return false;
        }
        catch
        {
            return false;
        }
    }
}
