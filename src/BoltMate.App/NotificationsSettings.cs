using System;
using System.Diagnostics;

namespace BoltMate.App;

/// <summary>
/// Best-effort helpers for the user-facing "Notifications" affordances in
/// the Settings UI. Both platforms gate notifications through OS-level
/// panels (Mac: System Settings → Notifications; Win: Settings →
/// Notifications &amp; actions) and neither exposes a fully reliable
/// programmatic state probe for unpackaged Win32 / NSUserNotification
/// apps — so this surface is intentionally minimal: a status hint + a
/// deeplink that drops the user on the right pane.
/// </summary>
public static class NotificationsSettings
{
    /// <summary>
    /// Opens the OS notification settings pane scoped to BoltMate where
    /// possible, or the top-level Notifications pane otherwise. Returns
    /// true if the launch was attempted (the OS may still ignore the
    /// deep-link silently).
    /// </summary>
    public static bool OpenOsSettings()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                // x-apple URL scheme — App-scoped pane. The id query param
                // matches our CFBundleIdentifier; when it's known to the
                // notification daemon (i.e. we've fired at least one
                // notification under this identity already), macOS deep-
                // links straight to BoltMate's row. Otherwise it opens the
                // Notifications top-level pane.
                Process.Start("open",
                    "x-apple.systempreferences:com.apple.preference.notifications?id=com.jaredballen.BoltMate");
                return true;
            }
            if (OperatingSystem.IsWindows())
            {
                // ms-settings URI handler — `notifications` is the top-level
                // pane. Per-app drill-down (`notifications-app?app=...`)
                // doesn't reliably work for unpackaged Win32 apps because
                // it expects a packaged AUMID; better to land on the top
                // pane where our entry is alphabetised under "BoltMate".
                var psi = new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true };
                Process.Start(psi);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort status label for the UI. Returns null when the platform
    /// has no reliable cheap probe — UI then falls back to "Managed in
    /// System Settings" copy without claiming a definite state.
    /// </summary>
    /// <remarks>
    /// macOS: NSUserNotification has no public auth-state API; the only
    /// reliable route is UNUserNotificationCenter.getNotificationSettings
    /// which is async + needs an Obj-C block. Deferred until we have a
    /// concrete need for live state. Win 10+ has
    /// <c>UserNotificationListener.AccessStatus</c> but only on the
    /// WinRT TFM, same blocker that pushed us to PowerShell for
    /// outbound toasts. For now both platforms return null.
    /// </remarks>
    public static bool? IsLikelyEnabled() => null;
}
