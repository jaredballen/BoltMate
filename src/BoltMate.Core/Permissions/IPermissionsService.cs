namespace BoltMate.Core.Permissions;

/// <summary>
/// App-wide aggregator of the three OS-gated capabilities BoltMate cares
/// about. One instance, injected into UI + background services. Implementations
/// own a single polling timer that pushes deltas to each permission's
/// <see cref="IPermission.IsGranted"/>.
/// </summary>
public interface IPermissionsService : IDisposable
{
    /// <summary>Local Network access (Mac TCC LocalNetwork; Win firewall + network profile).</summary>
    IPermission Network { get; }

    /// <summary>HID Input Monitoring. Mac-only; always Granted on Win/Linux.</summary>
    IPermission InputMonitoring { get; }

    /// <summary>Launch at login (Mac LaunchAgent; Win Task Scheduler onlogon).</summary>
    IPermission Autostart { get; }

    /// <summary>Force an immediate re-probe of every permission. Updates fire on change.</summary>
    void Refresh();
}
