namespace BoltMate.Core.Permissions;

/// <summary>
/// A single OS-gated capability the app needs (e.g. Local Network access,
/// HID Input Monitoring, launch-at-login). State is observable; the
/// Grant/Revoke pair drives the OS prompt OR routes the user to System
/// Settings when the OS owns the toggle.
/// </summary>
public interface IPermission
{
    /// <summary>Stable identifier — "network", "input-monitoring", "autostart".</summary>
    string Name { get; }

    /// <summary>Synchronous snapshot of the latest known state.</summary>
    bool IsGranted { get; }

    /// <summary>
    /// Stream of granted-state changes. Always emits the current value on
    /// subscribe (BehaviorSubject semantics), then forwards subsequent
    /// transitions.
    /// </summary>
    IObservable<bool> IsGrantedChanged { get; }

    /// <summary>
    /// Whether <see cref="RevokeAsync"/> can deterministically drive this
    /// permission to false. True only when the app owns the toggle (e.g.
    /// Autostart's LaunchAgent). For OS-owned TCC permissions this is false —
    /// only the user can revoke via System Settings.
    /// </summary>
    bool CanRevoke { get; }

    /// <summary>
    /// Drive the permission toward Granted. Dispatches the appropriate
    /// action (OS prompt for undecided, System Settings deep-link for
    /// previously-denied, plist install for autostart), then awaits the
    /// permission's <see cref="IsGrantedChanged"/> stream until it flips
    /// true or <paramref name="ct"/> trips.
    /// </summary>
    /// <returns>True if the permission reached Granted before cancellation; false otherwise.</returns>
    Task<bool> GrantAsync(CancellationToken ct = default);

    /// <summary>
    /// Drive the permission toward not-Granted. Only meaningful when
    /// <see cref="CanRevoke"/> is true; returns false immediately otherwise.
    /// Dispatches the revoke action (e.g. launchctl unload), then awaits
    /// <see cref="IsGrantedChanged"/> until it flips false or
    /// <paramref name="ct"/> trips.
    /// </summary>
    /// <returns>True if the permission reached not-Granted before cancellation; false otherwise or if revoke is unsupported.</returns>
    Task<bool> RevokeAsync(CancellationToken ct = default);

    /// <summary>
    /// Push empirical evidence that this permission IS granted, even when
    /// the OS-level probe disagrees. macOS's TCC per-process cache for
    /// <c>IOHIDCheckAccess</c> can return <c>Unknown</c> intermittently
    /// after running for a while, even though the actual grant is still
    /// in place and HID I/O continues to work. Successful device opens
    /// are proof of the real grant — call this from the transport layer
    /// when one happens so the cached <see cref="IsGranted"/> doesn't
    /// downgrade and trigger a false "Fix permissions" alert.
    /// </summary>
    /// <remarks>
    /// Once acknowledged, subsequent <c>ProbeOs</c> calls that return
    /// <c>Unknown</c> won't downgrade. An explicit <c>Denied</c> still
    /// does — if the user revokes the permission in System Settings, we
    /// want the alert to fire.
    /// </remarks>
    void AcknowledgeExternalGrant();
}
