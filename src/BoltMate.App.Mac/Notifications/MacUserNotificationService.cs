using BoltMate.App.Core.Notifications;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UserNotifications;

namespace BoltMate.App.Mac.Notifications;

/// <summary>
/// macOS <see cref="INotificationService"/> implementation backed by the
/// Microsoft.macOS bindings over <c>UNUserNotificationCenter</c>.
///
/// <para>
/// Grant flow chooses between two routes by current status:
/// <list type="bullet">
/// <item><b>NotDetermined</b> — fire the UN modal via
///   <c>requestAuthorization</c>. User picks Allow / Don't Allow.</item>
/// <item><b>Denied</b> — the modal can't re-fire; bounce to
///   <c>System Settings → Notifications → BoltMate</c> via deeplink so
///   the user can flip it manually.</item>
/// <item><b>Authorized</b> — already granted; no-op return true.</item>
/// </list>
/// Disable flow always goes to OS Settings (the app has no programmatic
/// revoke surface, and consistent platform UX is the point).
/// </para>
/// </summary>
public sealed class MacUserNotificationService : NotificationServiceBase
{
    private readonly ILogger<MacUserNotificationService> _log;

    private const UNAuthorizationOptions DefaultOptions =
        UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound;

    public MacUserNotificationService(ILogger<MacUserNotificationService>? logger = null)
    {
        _log = logger ?? NullLogger<MacUserNotificationService>.Instance;
    }

    public override NotificationAuthorizationStatus GetAuthorizationStatus()
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

    public override async Task<bool> RequestAuthorizationAsync(CancellationToken ct = default)
    {
        try
        {
            var current = GetAuthorizationStatus();
            switch (current)
            {
                case NotificationAuthorizationStatus.Authorized:
                case NotificationAuthorizationStatus.Provisional:
                    return true;

                case NotificationAuthorizationStatus.Denied:
                    // UN center won't re-show the modal after a denial.
                    // The only way back to Authorized is the user flipping
                    // it in System Settings. Open the pane so they can
                    // act; report false because the OS still says Denied.
                    _log.LogInformation("Authorization Denied — opening System Settings");
                    OpenOsSettings();
                    return false;

                default:
                    // NotDetermined → fire the modal. macOS shows it
                    // once per app per user; subsequent calls in any
                    // status other than NotDetermined are silent no-ops.
                    var (granted, error) = await UNUserNotificationCenter.Current
                        .RequestAuthorizationAsync(DefaultOptions);
                    if (error is not null)
                        _log.LogInformation("requestAuthorization returned error: {Error}", error.LocalizedDescription);
                    else
                        _log.LogInformation("requestAuthorization granted={Granted}", granted);
                    return granted;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RequestAuthorizationAsync threw");
            return false;
        }
    }

    protected override bool DeliverInternal(string title, string body)
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

    public override bool OpenOsSettings()
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
