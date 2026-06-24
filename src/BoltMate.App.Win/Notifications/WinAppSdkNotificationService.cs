using BoltMate.App.Core.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace BoltMate.App.Win.Notifications;

/// <summary>
/// Windows <see cref="INotificationService"/> implementation backed by
/// the Microsoft.WindowsAppSDK <c>AppNotificationManager</c>.
///
/// <para>
/// Follows Microsoft's official <c>CppUnpackagedAppNotifications</c> sample
/// exactly: subscribe to <c>NotificationInvoked</c>, call parameterless
/// <c>Register()</c>, that's it. Earlier iterations of this file tried to
/// inject an explicit AUMID via <c>SetCurrentProcessExplicitAppUserModelID</c>
/// and wrote the per-AUMID <c>Enabled</c> DWORD directly — both pushed the
/// SDK off the well-trodden path and produced
/// <c>Setting=DisabledForApplication</c> with no toast delivery. The
/// notification platform only fully trusts AUMIDs the SDK owns
/// end-to-end (CLSID + AppUserModelId entry + activator + platform
/// trust); injecting our own AUMID breaks that chain.
/// </para>
///
/// <para>
/// The user-facing display name "BoltMate" still surfaces in OS Settings
/// because <c>Register()</c> writes <c>DisplayName=BoltMate</c> into the
/// SDK-owned AppUserModelId entry. The opaque AUMID (path-hash) is an
/// internal detail neither user nor app needs to care about.
/// </para>
/// </summary>
public sealed class WinAppSdkNotificationService : NotificationServiceBase, IDisposable
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
            _log.LogInformation("AppNotificationManager.Register() succeeded — Setting={Setting}",
                AppNotificationManager.Default.Setting);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AppNotificationManager.Register() failed");
        }
    }

    public override NotificationAuthorizationStatus GetAuthorizationStatus()
    {
        try
        {
            return AppNotificationManager.Default.Setting switch
            {
                AppNotificationSetting.Enabled => NotificationAuthorizationStatus.Authorized,
                // Every "disabled" subspecies (group policy, per-app, focus
                // assist, global) collapses to Denied for our UI purposes.
                _ => NotificationAuthorizationStatus.Denied,
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AppNotificationManager.Setting read failed");
            return NotificationAuthorizationStatus.NotDetermined;
        }
    }

    public override Task<bool> RequestAuthorizationAsync(CancellationToken ct = default)
    {
        // No programmatic grant API on Win. SDK Register() (run from the
        // ctor) already established our entry; if Setting is anything
        // other than Enabled, the user has revoked in System Settings.
        // Open the pane so the click has somewhere meaningful to land.
        OpenOsSettings();
        return Task.FromResult(GetAuthorizationStatus() is NotificationAuthorizationStatus.Authorized);
    }

    protected override bool DeliverInternal(string title, string body)
    {
        try
        {
            EnsureRegistered();
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            _log.LogDebug("Notification dispatched: Id={Id}", notification.Id);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AppNotificationManager.Show threw");
            return false;
        }
    }

    public override bool OpenOsSettings()
    {
        try
        {
            // Top-level Notifications pane. BoltMate is alphabetised in
            // the list under "Notifications from apps and other senders".
            var psi = new System.Diagnostics.ProcessStartInfo("ms-settings:notifications")
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
