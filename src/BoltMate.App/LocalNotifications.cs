using System;
using BoltMate.App.Services;

namespace BoltMate.App;

/// <summary>
/// Best-effort local notification surface. The OS — not us — gates
/// delivery: UNUserNotificationCenter on macOS drops posts silently
/// when the user has denied authorisation; Windows Settings → System →
/// Notifications → BoltMate does the same on Win. We post unconditionally
/// and trust the OS to honour the user's choice.
/// </summary>
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
