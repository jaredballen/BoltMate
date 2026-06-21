namespace BoltMate.App;

/// <summary>Aggregate roll-up across every permission the app cares about.</summary>
/// <remarks>
/// Source of truth is <see cref="BoltMate.Core.Permissions.IPermissionsService"/>;
/// this enum is the tray-badge / notification-routing summary derived from
/// the per-permission booleans in App.axaml.cs.
/// </remarks>
public enum OverallStatus
{
    AllGood,
    AnyDenied,
}
