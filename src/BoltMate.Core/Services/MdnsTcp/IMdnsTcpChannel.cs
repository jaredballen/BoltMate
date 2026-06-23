using BoltMate.Core.Topology;

namespace BoltMate.Core.Services;

/// <summary>
/// Bonjour / mDNS service-discovery + TCP backchannel that mirrors the UDP
/// topology stream. Lets peers find each other even when LAN multicast is
/// blocked, and gives the UI two independent health signals (mDNS vs TCP).
/// </summary>
public interface IMdnsTcpChannel : IAsyncDisposable, IDisposable
{
    /// <summary>Bonjour publisher / browser health.</summary>
    IObservable<TransportHealth> MdnsHealth { get; }

    /// <summary>TCP listener + peer-connection health.</summary>
    IObservable<TransportHealth> TcpHealth { get; }

    /// <summary>Combined health for the UI roll-up — Blocked if either is.</summary>
    IObservable<TransportHealth> SyncHealth { get; }

    /// <summary>Idempotently starts mDNS publish + browse + TCP listener.</summary>
    void Start();
}
