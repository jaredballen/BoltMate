using BoltMate.Core.Topology;

namespace BoltMate.Core.Services;

/// <summary>
/// Cross-machine fan-out trigger. Filters inbound peer announcements down
/// to the ones that reference our host name, then reacts to
/// <see cref="ReceiverAnnouncement.LastSwitchEvent"/> + remote
/// reappearance-after-link-lost to fan our local siblings.
/// </summary>
public interface ITopologyCorrelator : IDisposable
{
    /// <summary>
    /// Peer announcements that passed the host-name prune filter (i.e. have
    /// at least one device referencing us in its slot map). Surfaced for UI
    /// + tests.
    /// </summary>
    IObservable<ReceiverAnnouncement> FilteredAnnouncements { get; }
}
