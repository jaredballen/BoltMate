using System;
using BoltMate.App.Services;
using BoltMate.Core;

namespace BoltMate.App;

/// <summary>
/// Best-effort local notification surface, gated by the tri-state
/// <see cref="NotificationsState"/> persisted in <see cref="AppSettings"/>.
/// <list type="bullet">
///   <item><c>Disabled</c> → <see cref="TryPost"/> is a no-op. Honour the
///         user's opt-out absolutely.</item>
///   <item><c>UserRequested</c> → post normally. On Mac the OS shows its
///         reactive "Allow / Don't Allow" banner on first delivery.</item>
///   <item><c>OSGranted</c> → post normally. State transitions are driven
///         by <see cref="Services.PermissionsService"/> observing the
///         OS notification settings (currently stubbed on Mac until the
///         UN bridge lands).</item>
/// </list>
/// </summary>
public static class LocalNotifications
{
    /// <summary>
    /// Caller-supplied current preference. Set by the App layer once
    /// settings are loaded; the gate falls back to "Disabled" until then
    /// so we don't fire stray notifications during bootstrap.
    /// </summary>
    public static Func<NotificationsState>? StateProvider { get; set; }

    /// <summary>
    /// Show a notification. Returns true if the call was dispatched —
    /// not a guarantee the banner actually rendered (auth status,
    /// Focus / Do Not Disturb, etc. can intercept downstream). Returns
    /// false (and skips the OS call entirely) when state is Disabled.
    /// </summary>
    public static bool TryPost(string title, string body)
    {
        if ((StateProvider?.Invoke() ?? NotificationsState.Disabled) is NotificationsState.Disabled)
            return false;
        return PostUnconditional(title, body);
    }

    /// <summary>
    /// Posts without consulting <see cref="StateProvider"/>. Used by the
    /// Settings test-notification button to verify the wiring even when
    /// the user has the preference toggled off — gives them a way to
    /// observe the OS prompt without flipping the persisted setting.
    /// </summary>
    public static bool PostUnconditional(string title, string body)
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
