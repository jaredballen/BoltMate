namespace BoltMate.Core.Services;

/// <summary>
/// Multi-receiver fan-out orchestrator. Listens to every attached
/// receiver's host-switch streams, routes the press / flow-snoop across
/// the manager-scoped device set, and offers a user-callable fan-out
/// entry point for CLI / cross-machine correlation.
/// </summary>
public interface ISwitcherService : IDisposable
{
    /// <summary>One emission per successful sibling CHANGE_HOST write.</summary>
    IObservable<FanOutEvent> FanOuts { get; }

    /// <summary>
    /// One emission per Easy-Switch press / Flow snoop / user request, fired
    /// after the target hostname has been resolved but BEFORE the local
    /// sibling loop. Topology layer consumes this to broadcast intent to peers.
    /// </summary>
    IObservable<LocalSwitchTrigger> LocalSwitchTriggers { get; }

    /// <summary>
    /// Fans every eligible local sibling to <paramref name="targetHostName"/>,
    /// skipping the originator (matched by wpid) and devices without
    /// HostBindings to the target. Returns the number of CHANGE_HOST writes
    /// issued.
    /// </summary>
    int RequestTopologyFanOut(string targetHostName, ushort? originatingDeviceWpid, FanOutSource source = FanOutSource.RemoteTopology);
}
