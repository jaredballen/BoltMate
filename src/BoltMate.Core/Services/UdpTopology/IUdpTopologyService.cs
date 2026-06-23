using BoltMate.Core.Topology;

namespace BoltMate.Core.Services;

/// <summary>
/// UDP-broadcast topology service. Emits per-machine
/// <see cref="ReceiverAnnouncement"/>s on a loop, ingests peer announcements,
/// surfaces self-echo health, and records local switch events that piggy-back
/// onto the next outbound announcement.
/// </summary>
public interface IUdpTopologyService : IAsyncDisposable, IDisposable
{
    /// <summary>Stable per-machine ID this service broadcasts under.</summary>
    string MachineId { get; }

    /// <summary>Total outbound attempts + errors (lifetime counters).</summary>
    (long Attempts, long Errors) SendStats { get; }

    /// <summary>Stream of peer announcements that passed the dedup filter.</summary>
    IObservable<ReceiverAnnouncement> Announcements { get; }

    /// <summary>Stream of every announcement WE emit (for backchannel mirroring).</summary>
    IObservable<ReceiverAnnouncement> OutgoingAnnouncements { get; }

    /// <summary>Health signal — Healthy / Blocked based on self-echo cadence.</summary>
    IObservable<TransportHealth> UdpHealth { get; }

    /// <summary>Snapshot of per-peer statistics.</summary>
    IReadOnlyCollection<PeerStats> PeerSnapshot { get; }

    /// <summary>Most recent announcement seen from each peer.</summary>
    IReadOnlyCollection<ReceiverAnnouncement> LatestPeerAnnouncements { get; }

    /// <summary>Begin the broadcast loop + open the socket. Idempotent.</summary>
    void Start();

    /// <summary>
    /// Settings-driven pause. Closes the socket + cancels loops but
    /// keeps the service instance alive — a subsequent <see cref="Start"/>
    /// rebinds cleanly. Use this for the topology on/off toggle so the
    /// DI singleton lifecycle stays simple.
    /// </summary>
    void Stop();

    /// <summary>Inject an announcement received via an external channel (mDNS+TCP).</summary>
    void InjectInbound(ReceiverAnnouncement announcement, string channel = "ext");

    /// <summary>
    /// Stash a switch event to surface on the next outbound announcement's
    /// <see cref="ReceiverAnnouncement.LastSwitchEvent"/>.
    /// </summary>
    void RecordLocalSwitchEvent(string? deviceSerial, string targetHostName);
}
