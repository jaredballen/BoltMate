using BoltMate.App.Core.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace BoltMate.App.Win.Notifications;

/// <summary>
/// Windows <see cref="INotificationService"/> implementation backed by
/// the Microsoft.WindowsAppSDK. Replaces the previous PowerShell + WinRT
/// projection shim that was brittle around process lifetime, async toast
/// commit, and AUMID registration. The SDK handles all three internally:
///   <list type="bullet">
///     <item><see cref="AppNotificationManager.Register()"/> performs the
///       AUMID + activator registration for unpackaged Win32 apps. After
///       this call the app shows up in Settings → System → Notifications
///       → Apps and toasts route under the correct identity.</item>
///     <item><see cref="AppNotificationManager.Show(AppNotification)"/> is
///       an in-process synchronous WinRT call — no spawned subprocess to
///       race, no async commit window to bridge.</item>
///     <item><see cref="AppNotificationManager.Setting"/> exposes a clean
///       tri-state status enum that maps to our
///       <see cref="NotificationAuthorizationStatus"/> without any
///       registry-probe acrobatics.</item>
///   </list>
/// </summary>
public sealed class WinAppSdkNotificationService : INotificationService, IDisposable
{
    private readonly ILogger<WinAppSdkNotificationService> _log;
    private bool _registered;
    private bool _disposed;

    public WinAppSdkNotificationService(ILogger<WinAppSdkNotificationService>? logger = null)
    {
        _log = logger ?? NullLogger<WinAppSdkNotificationService>.Instance;
        EnsureRegistered();
    }

    private void EnsureRegistered()
    {
        if (_registered) return;
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
            _log.LogInformation("AppNotificationManager.Register() succeeded");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AppNotificationManager.Register() failed — Win toasts may not appear in Settings");
        }
    }

    public NotificationAuthorizationStatus GetAuthorizationStatus()
    {
        try
        {
            var setting = AppNotificationManager.Default.Setting;
            return setting switch
            {
                AppNotificationSetting.Enabled => NotificationAuthorizationStatus.Authorized,
                // Every "disabled" subspecies (group policy, per-app, focus
                // assist, global) all collapse to Denied from BoltMate's POV
                // — we can't do anything about which one beyond pointing
                // the user at System Settings.
                _ => NotificationAuthorizationStatus.Denied,
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AppNotificationManager.Setting read failed");
            return NotificationAuthorizationStatus.NotDetermined;
        }
    }

    public Task<bool> RequestAuthorizationAsync(CancellationToken ct = default)
    {
        // Windows has no modal authorisation prompt — first toast is the
        // user's introduction. Best we can do for "Request" is open the
        // Settings pane so the user can confirm / flip it. The actual
        // auth-state observation happens via the next probe.
        OpenOsSettings();
        return Task.FromResult(GetAuthorizationStatus() is NotificationAuthorizationStatus.Authorized);
    }

    public bool Deliver(string title, string body)
    {
        try
        {
            EnsureRegistered();
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            _log.LogDebug("Notification dispatched");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AppNotificationManager.Show threw");
            return false;
        }
    }

    public bool OpenOsSettings()
    {
        try
        {
            // ms-settings:notifications-app?app=BoltMate is the per-app
            // pane. Win 11 honours it for AUMID-registered apps; if it
            // doesn't resolve the OS falls back to the top-level
            // Notifications pane where BoltMate is alphabetised after we
            // get Microsoft.WindowsAppSDK to register us.
            var psi = new System.Diagnostics.ProcessStartInfo("ms-settings:notifications-app?app=BoltMate")
            {
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to open notification settings");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registered)
        {
            try { AppNotificationManager.Default.Unregister(); }
            catch (Exception ex) { _log.LogDebug(ex, "Unregister threw"); }
        }
    }
}
