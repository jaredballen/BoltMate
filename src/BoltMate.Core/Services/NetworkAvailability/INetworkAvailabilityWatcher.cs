namespace BoltMate.Core.Services;

/// <summary>
/// Surfaces the OS's "any NIC up + connected" signal as a reactive
/// stream. Distinct from permission gates — even with Local Network
/// fully granted, a disabled adapter still leaves us with nowhere
/// to bind. Topology services subscribe to this to park themselves
/// when no usable interface exists and resume on reconnect.
/// </summary>
public interface INetworkAvailabilityWatcher : IDisposable
{
    /// <summary>Current state — true when the OS believes at least one NIC is up.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Stream of availability changes. BehaviorSubject semantics —
    /// every subscribe receives the current value, then forwards
    /// subsequent transitions.
    /// </summary>
    IObservable<bool> IsAvailableChanged { get; }
}
