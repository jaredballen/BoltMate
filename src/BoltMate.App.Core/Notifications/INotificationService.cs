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
    /// Fires the OS authorisation prompt (Mac: native modal; Win: opens the
    /// notification settings pane since Win has no equivalent modal API).
    /// Returns whether the OS reports authorisation after the call.
    /// </summary>
    Task<bool> RequestAuthorizationAsync(CancellationToken ct = default);

    /// <summary>
    /// Schedules a notification for immediate delivery. Auth-gated by the
    /// OS — denied posts drop silently. Returns true if the call was
    /// dispatched without error.
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
