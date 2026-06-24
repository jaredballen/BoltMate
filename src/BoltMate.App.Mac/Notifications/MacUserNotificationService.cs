using BoltMate.App.Core.Notifications;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UserNotifications;

namespace BoltMate.App.Mac.Notifications;

/// <summary>
/// macOS <see cref="INotificationService"/> implementation backed by the
/// Microsoft.macOS bindings over <c>UNUserNotificationCenter</c>. Mirrors
/// the line-for-line approach the user pointed at in their other project
/// (lci-ids/Permissions/.../NotificationsPermission.macios.cs) — managed
/// completion handlers, idiomatic async/await, no ObjC block ABI
/// hand-rolling.
/// </summary>
public sealed class MacUserNotificationService : INotificationService
{
    private readonly ILogger<MacUserNotificationService> _log;

    private const UNAuthorizationOptions DefaultOptions =
        UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound;

    public MacUserNotificationService(ILogger<MacUserNotificationService>? logger = null)
    {
        _log = logger ?? NullLogger<MacUserNotificationService>.Instance;
    }

    public NotificationAuthorizationStatus GetAuthorizationStatus()
    {
        try
        {
            // GetNotificationSettings is async-only at the UN API surface.
            // Wrap with a semaphore so the synchronous probe pipeline in
            // PermissionBase.ProbeOs can drive it without async plumbing.
            UNAuthorizationStatus status = UNAuthorizationStatus.NotDetermined;
            using var sem = new System.Threading.SemaphoreSlim(0, 1);
            UNUserNotificationCenter.Current.GetNotificationSettings(settings =>
            {
                if (settings is not null) status = settings.AuthorizationStatus;
                sem.Release();
            });
            if (!sem.Wait(TimeSpan.FromSeconds(2)))
            {
                _log.LogWarning("GetNotificationSettings callback didn't fire within 2s");
                return NotificationAuthorizationStatus.NotDetermined;
            }
            return Map(status);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GetAuthorizationStatus threw");
            return NotificationAuthorizationStatus.NotDetermined;
        }
    }

    public async Task<bool> RequestAuthorizationAsync(CancellationToken ct = default)
    {
        try
        {
            var (granted, error) = await UNUserNotificationCenter.Current
                .RequestAuthorizationAsync(DefaultOptions);
            if (error is not null)
                _log.LogInformation("requestAuthorization returned error: {Error}", error.LocalizedDescription);
            else
                _log.LogInformation("requestAuthorization granted={Granted}", granted);
            return granted;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RequestAuthorizationAsync threw");
            return false;
        }
    }

    public bool Deliver(string title, string body)
    {
        try
        {
            var content = new UNMutableNotificationContent
            {
                Title = title,
                Body  = body,
                Sound = UNNotificationSound.Default,
            };
            var request = UNNotificationRequest.FromIdentifier(
                identifier: Guid.NewGuid().ToString("N"),
                content: content,
                trigger: null);
            UNUserNotificationCenter.Current.AddNotificationRequest(request, error =>
            {
                if (error is not null)
                    _log.LogWarning("AddNotificationRequest error: {Error}", error.LocalizedDescription);
            });
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Deliver threw");
            return false;
        }
    }

    public bool OpenOsSettings()
    {
        try
        {
            // x-apple URL handler — deep-links to BoltMate's row in the
            // Notifications pane when the bundle identifier is recognised.
            using var url = new NSUrl(
                "x-apple.systempreferences:com.apple.preference.notifications?id=com.jaredballen.BoltMate");
            return AppKit.NSWorkspace.SharedWorkspace.OpenUrl(url);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OpenOsSettings threw");
            return false;
        }
    }

    private static NotificationAuthorizationStatus Map(UNAuthorizationStatus status) => status switch
    {
        UNAuthorizationStatus.Authorized    => NotificationAuthorizationStatus.Authorized,
        UNAuthorizationStatus.Provisional   => NotificationAuthorizationStatus.Provisional,
        UNAuthorizationStatus.Denied        => NotificationAuthorizationStatus.Denied,
        // Ephemeral is iOS-only (App Clips); macOS won't emit it.
        _                                   => NotificationAuthorizationStatus.NotDetermined,
    };
}
