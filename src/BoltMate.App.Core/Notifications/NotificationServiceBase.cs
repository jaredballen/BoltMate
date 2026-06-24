namespace BoltMate.App.Core.Notifications;

/// <summary>
/// Shared base for platform notification services. Owns the OS-auth
/// gate inside <see cref="Deliver"/> so neither platform impl needs
/// to re-implement the check. Platforms only provide
/// <see cref="DeliverInternal"/> + the three OS-facing primitives.
/// </summary>
public abstract class NotificationServiceBase : INotificationService
{
    public bool Deliver(string title, string body)
    {
        // Single gate: OS authorisation. The previous "AppEnabled" axis
        // was removed in favour of one source of truth — the OS — so
        // any caller can fire Deliver without checking auth first.
        var status = GetAuthorizationStatus();
        if (status is not NotificationAuthorizationStatus.Authorized
                   and not NotificationAuthorizationStatus.Provisional)
            return false;

        return DeliverInternal(title, body);
    }

    /// <summary>
    /// Platform-specific actual delivery. Called by <see cref="Deliver"/>
    /// only after the OS-auth gate passes.
    /// </summary>
    protected abstract bool DeliverInternal(string title, string body);

    public abstract NotificationAuthorizationStatus GetAuthorizationStatus();
    public abstract Task<bool> RequestAuthorizationAsync(CancellationToken ct = default);
    public abstract bool OpenOsSettings();
}
