namespace BoltMate.App.Core.Notifications;

/// <summary>
/// Cross-platform notification dispatch + authorisation surface.
/// Implementations live in platform projects:
///   <list type="bullet">
///     <item><see cref="BoltMate.App.Mac"/> — Microsoft.macOS bindings over
///       <c>UNUserNotificationCenter</c>.</item>
///     <item><see cref="BoltMate.App.Win"/> — Microsoft.WindowsAppSDK
///       <c>AppNotificationManager</c> + <c>AppNotificationBuilder</c>.</item>
///   </list>
/// Selected by DI at composition time via the running OS.
///
/// <para>
/// <b>Design:</b> the OS is the single source of truth for "may BoltMate
/// post notifications?". There is no separate app-level pref — the
/// in-app toggle is a thin view over the OS state, and click-to-enable
/// drives the OS (Win: HKCU reg write; Mac: UN modal or Settings
/// deeplink). Click-to-disable always defers to OS Settings — the app
/// never revokes its own grant — so users always end up in the same OS
/// pane regardless of which UI surfaced the action.
/// </para>
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Probes the OS for the app's current authorisation status. Synchronous
    /// returns are easier to plug into the existing PermissionBase probe
    /// pipeline, so platform impls semaphore-wait their async APIs
    /// internally if needed.
    /// </summary>
    NotificationAuthorizationStatus GetAuthorizationStatus();

    /// <summary>
    /// Attempt to escalate to <see cref="NotificationAuthorizationStatus.Authorized"/>.
    /// Platform-specific:
    /// <list type="bullet">
    /// <item><b>Win</b> — writes the HKCU registry slot the OS reads
    ///   (<c>Software\Microsoft\Windows\CurrentVersion\Notifications\
    ///   Settings\&lt;AUMID&gt;\Enabled</c>) to 1. The 1Hz probe picks
    ///   up the change and re-emits.</item>
    /// <item><b>Mac</b> — when status is <c>NotDetermined</c>, fires the
    ///   <c>UNUserNotificationCenter.requestAuthorization</c> modal.
    ///   When status is <c>Denied</c> (the modal can't re-fire), opens
    ///   <c>System Settings → Notifications → BoltMate</c> via deeplink
    ///   so the user can flip it manually. Already-<c>Authorized</c>
    ///   returns true synchronously without any UI surface.</item>
    /// </list>
    /// Returns whether the OS reports Authorized after the call. Callers
    /// shouldn't trust this as the final state — the probe will report
    /// the canonical value within ~1s.
    /// </summary>
    Task<bool> RequestAuthorizationAsync(CancellationToken ct = default);

    /// <summary>
    /// Schedules a notification for immediate delivery. Silently drops
    /// when the OS has not granted authorisation. Returns true when a
    /// notification was actually dispatched.
    /// </summary>
    bool Deliver(string title, string body);

    /// <summary>
    /// Opens the OS notification settings pane scoped to BoltMate where
    /// possible, or the top-level pane otherwise. Returns true if the
    /// launch was attempted.
    /// </summary>
    bool OpenOsSettings();
}

/// <summary>
/// Mirror of macOS's <c>UNAuthorizationStatus</c> enum, also used to
/// represent Windows registry state (Authorized when Enabled=1, Denied
/// when Enabled=0, NotDetermined when absent).
/// </summary>
public enum NotificationAuthorizationStatus
{
    NotDetermined = 0,
    Denied        = 1,
    Authorized    = 2,
    /// <summary>macOS only — quiet delivery; treat as Authorized.</summary>
    Provisional   = 3,
    /// <summary>macOS only — App Clips, not applicable to BoltMate.</summary>
    Ephemeral     = 4,
}
