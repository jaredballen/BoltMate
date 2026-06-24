using System;
using BoltMate.App.Core.Notifications;

namespace BoltMate.App;

/// <summary>
/// Static facade over <see cref="INotificationService"/> for callers that
/// don't have direct access to DI (the AppHealthService → tray callback
/// path in App.axaml.cs, the Settings test-notification command, etc).
/// The App layer sets <see cref="Service"/> at bootstrap after the
/// container materialises the platform-specific impl.
/// </summary>
public static class LocalNotifications
{
    /// <summary>
    /// Platform impl set by App bootstrap. Null until ServiceRegistration
    /// has resolved — calls before then no-op so we don't crash trying to
    /// post toasts during startup.
    /// </summary>
    public static INotificationService? Service { get; set; }

    public static bool TryPost(string title, string body)
    {
        try
        {
            return Service?.Deliver(title, body) ?? false;
        }
        catch
        {
            return false;
        }
    }
}
