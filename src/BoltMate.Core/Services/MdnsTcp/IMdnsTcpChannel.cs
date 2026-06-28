using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.Core.Topology;
using BoltMate.Core.Topology.Messages;

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

    /// <summary>
    /// Settings-driven pause. Releases mDNS publisher, browser, listener,
    /// and any open peer TcpClients without disposing — a subsequent
    /// <see cref="Start"/> rebinds cleanly.
    /// </summary>
    void Stop();

    /// <summary>
    /// Callback invoked when an inbound <see cref="LogBundleRequest"/>
    /// arrives. The App layer wires this to its <c>LogBundler</c>; the
    /// returned bytes become the <see cref="LogBundleResponse.ZipBase64"/>
    /// payload. Null = decline (responder will reply with an Error).
    /// </summary>
    Func<CancellationToken, Task<byte[]?>>? LogBundleProvider { get; set; }

    /// <summary>
    /// Broadcasts a <see cref="LogBundleRequest"/> to every currently-
    /// connected peer and collects responses up to <paramref name="timeout"/>.
    /// Returns the responses that arrived before the timeout; peers that
    /// don't reply are simply omitted (no entry in the result list).
    /// </summary>
    Task<IReadOnlyList<LogBundleResponse>> RequestPeerLogsAsync(TimeSpan timeout, CancellationToken ct = default);
}
