using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using Makaretu.Dns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.Core.Topology;

/// <summary>
/// Parallel-to-UDP transport for topology announcements. Uses mDNS to
/// discover peers (publishes <c>_boltmate._udp.local</c> with a TXT record
/// carrying our machineId + TCP port) and TCP to deliver the same
/// announcement payload reliably to each discovered peer.
/// </summary>
/// <remarks>
/// Architecture:
/// <list type="bullet">
///   <item>One TCP listener bound to <see cref="TopologySettings.TcpPort"/>.
///         Accepts inbound peer connections and reads length-prefixed JSON
///         announcements, feeding them into the parent UDP service's dedup
///         pipeline via <see cref="UdpTopologyService.InjectInbound"/>.</item>
///   <item>One mDNS publisher advertising our service so peers find us.</item>
///   <item>One mDNS browser discovering peers' service records.</item>
///   <item>Per-discovered-peer, an outbound TcpClient is opened on demand
///         and kept alive. All outgoing announcements (subscribed from
///         <see cref="UdpTopologyService.OutgoingAnnouncements"/>) are
///         length-prefixed and written to every connected peer.</item>
/// </list>
/// </remarks>
public sealed class MdnsTcpChannel : IDisposable
{
    private readonly UdpTopologyService _udp;
    private readonly TopologySettings _settings;
    private readonly string _machineId;
    private readonly ILogger<MdnsTcpChannel> _logger;
    private readonly CompositeDisposable _disposables = new();
    private readonly ConcurrentDictionary<string, TcpClient> _peerClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();

    private TcpListener? _listener;
    private MulticastService? _multicast;
    private ServiceDiscovery? _serviceDiscovery;
    private bool _disposed;

    // mDNS self-echo health. We auto-receive answers for our OWN advertised
    // service through the same MulticastService — if Bonjour is healthy on
    // the LAN, our instance comes back to us within seconds of the periodic
    // re-advert. If Local Network is denied / Bonjour service isn't running
    // on Windows / multicast is blocked, no echo arrives. Used to surface
    // "Bonjour blocked" independently from the LAN UDP path.
    private DateTimeOffset _lastSelfMdnsEcho;
    private DateTimeOffset _mdnsStartedAt;
    private readonly System.Reactive.Subjects.BehaviorSubject<TransportHealth> _mdnsHealth;
    private System.Threading.Timer? _mdnsHealthTimer;
    private TransportState _lastMdnsState = TransportState.Unknown;
    private static readonly TimeSpan MdnsEchoFreshness = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MdnsWarmup = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Self-echo health of the mDNS / Bonjour discovery path. Independent
    /// from <see cref="UdpTopologyService.UdpHealth"/> so users can tell
    /// which transport is failing — e.g. UDP blocked but Bonjour fine, or
    /// vice versa.
    /// </summary>
    public IObservable<TransportHealth> MdnsHealth => _mdnsHealth.AsObservable();

    // TCP backchannel health. Tracks "Bonjour discovered N peer(s) but we
    // can't open the TCP port to them" — a different failure mode from a
    // blocked discovery: discovery works but the per-port firewall rule
    // on the peer rejects the connection.
    private long _tcpConnectAttempts;
    private long _tcpConnectFailures;
    private DateTimeOffset _lastTcpConnectSuccess;
    private DateTimeOffset _lastPeerDiscovered;
    private string? _lastTcpFailureMessage;
    private readonly System.Reactive.Subjects.BehaviorSubject<TransportHealth> _tcpHealth;
    private TransportState _lastTcpState = TransportState.Unknown;
    private static readonly TimeSpan TcpConnectGrace = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Health of the TCP backchannel used to deliver announcements
    /// reliably between Bonjour-discovered peers. Independent from
    /// <see cref="MdnsHealth"/> so we can distinguish "Bonjour discovery
    /// works but the peer's TCP port is blocked" from "Bonjour itself
    /// is broken."
    /// </summary>
    public IObservable<TransportHealth> TcpHealth => _tcpHealth.AsObservable();

    public MdnsTcpChannel(
        UdpTopologyService udp,
        TopologySettings settings,
        string machineId,
        ILogger<MdnsTcpChannel>? logger = null)
    {
        _udp = udp;
        _settings = settings;
        _machineId = machineId;
        _logger = logger ?? NullLogger<MdnsTcpChannel>.Instance;
        _mdnsHealth = new System.Reactive.Subjects.BehaviorSubject<TransportHealth>(
            TransportHealth.Unknown(MdnsEndpointLabel(), "Bonjour publisher not started yet"));
        _tcpHealth = new System.Reactive.Subjects.BehaviorSubject<TransportHealth>(
            TransportHealth.Unknown(TcpEndpointLabel(), "no Bonjour peers discovered yet"));
        _disposables.Add(_mdnsHealth);
        _disposables.Add(_tcpHealth);
    }

    private string TcpEndpointLabel() => $"TCP backchannel (port {_settings.TcpPort})";

    private void EmitTcpHealth(TransportState state, string detail)
    {
        if (_lastTcpState == state) return;
        _lastTcpState = state;
        _tcpHealth.OnNext(new TransportHealth(state, TcpEndpointLabel(), detail, DateTimeOffset.UtcNow));
    }

    private void RecomputeTcpHealth()
    {
        // No peers discovered yet → can't say whether we'd reach them.
        if (_lastPeerDiscovered == default)
        {
            EmitTcpHealth(TransportState.Unknown, "no Bonjour peers discovered yet");
            return;
        }

        // Currently-connected client count is the strongest positive signal.
        var connected = 0;
        foreach (var c in _peerClients.Values) if (c.Connected) connected++;
        if (connected > 0)
        {
            EmitTcpHealth(TransportState.Healthy,
                $"connected to {connected} peer{(connected == 1 ? "" : "s")} via TCP");
            return;
        }

        // No connection right now. If it's been long enough since the first
        // discovery for at least one connect attempt to have settled, call it
        // Blocked — otherwise we're still mid-attempt.
        var sinceDiscover = DateTimeOffset.UtcNow - _lastPeerDiscovered;
        if (sinceDiscover < TcpConnectGrace)
        {
            EmitTcpHealth(TransportState.Unknown, $"connect attempts in flight ({sinceDiscover.TotalSeconds:F0}s since discovery)");
            return;
        }

        EmitTcpHealth(TransportState.Blocked,
            $"discovered peer(s) via Bonjour but couldn't open TCP port {_settings.TcpPort}. " +
            "The peer likely has a firewall inbound rule blocking the port — verify BoltMate is allowed through Windows Defender Firewall / macOS Local Network access on that machine. " +
            (_lastTcpFailureMessage is null ? "" : $"Last error: {_lastTcpFailureMessage}"));
    }

    private string MdnsEndpointLabel() =>
        $"{NormaliseServiceName(_settings.MdnsServiceType)} (mDNS 224.0.0.251:5353)";

    private void EmitMdnsHealth(TransportState state, string detail)
    {
        if (_lastMdnsState == state) return;
        _lastMdnsState = state;
        _mdnsHealth.OnNext(new TransportHealth(state, MdnsEndpointLabel(), detail, DateTimeOffset.UtcNow));
    }

    private void RecomputeMdnsHealth()
    {
        if (_multicast is null || _serviceDiscovery is null)
        {
            EmitMdnsHealth(TransportState.Blocked,
                "Bonjour publisher failed to start. On Windows: confirm the 'Bonjour Service' is running. On macOS: confirm Local Network access is granted.");
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var sinceStart = now - _mdnsStartedAt;
        var sinceEcho = now - _lastSelfMdnsEcho;
        if (_lastSelfMdnsEcho == default && sinceStart < MdnsWarmup)
        {
            EmitMdnsHealth(TransportState.Unknown, $"warming up ({sinceStart.TotalSeconds:F0}/{MdnsWarmup.TotalSeconds:F0}s)");
            return;
        }
        if (_lastSelfMdnsEcho == default || sinceEcho > MdnsEchoFreshness)
        {
            EmitMdnsHealth(TransportState.Blocked,
                $"Bonjour service has not echoed our own advert in {(sinceEcho == default ? sinceStart : sinceEcho).TotalSeconds:F0}s. " +
                "On Windows: confirm the 'Bonjour Service' is running. On macOS: confirm Local Network access is granted. " +
                "If both look right, multicast (224.0.0.251:5353) may be filtered on this network.");
            return;
        }
        EmitMdnsHealth(TransportState.Healthy, $"last self-echo {sinceEcho.TotalSeconds:F0}s ago");
    }

    /// <summary>Idempotently starts the mDNS publisher, browser, and TCP listener.</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MdnsTcpChannel));
        if (_listener is not null) return;

        // Wrap EVERYTHING — any unexpected throw from Makaretu.Dns (e.g.
        // UDP 5353 bind contention on Windows when Bonjour service is
        // already running) must not take down the host app process.
        try
        {
            StartCore();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MdnsTcp.Start failed catastrophically — channel disabled this session");
        }
    }

    private void StartCore()
    {
        // 1. TCP listener — peers will connect to us once they've discovered
        //    us via mDNS. We accept and read length-prefixed JSON.
        try
        {
            _listener = new TcpListener(IPAddress.Any, _settings.TcpPort);
            _listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _logger.LogInformation("MdnsTcp: listening on TCP port {Port}", _settings.TcpPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MdnsTcp: TCP bind on port {Port} failed; mDNS+TCP transport disabled this session",
                _settings.TcpPort);
            _listener = null;
            return;
        }

        // 2. mDNS publisher + browser. Each sub-step wrapped — different
        //    failure modes on different platforms (Win Bonjour conflict on
        //    UDP 5353, macOS Privacy & Security prompts, Linux Avahi absent).
        try
        {
            _multicast = new MulticastService();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MdnsTcp: MulticastService init failed; mDNS disabled, TCP listener still up");
            return;
        }

        try
        {
            _serviceDiscovery = new ServiceDiscovery(_multicast);
            var service = new ServiceProfile(
                instanceName: _machineId,
                serviceName: NormaliseServiceName(_settings.MdnsServiceType),
                port: (ushort)_settings.TcpPort);
            service.AddProperty("machineId", _machineId);
            service.AddProperty("udpPort", _settings.Port.ToString());
            service.AddProperty("v", "1");
            _serviceDiscovery.Advertise(service);
            _serviceDiscovery.ServiceDiscovered += OnServiceDiscovered;
            _serviceDiscovery.ServiceInstanceDiscovered += OnInstanceDiscovered;
            _multicast.Start();
            _serviceDiscovery.QueryServiceInstances(NormaliseServiceName(_settings.MdnsServiceType));
            _mdnsStartedAt = DateTimeOffset.UtcNow;

            // Periodic re-query keeps the self-echo signal fresh — without
            // this we'd depend purely on Makaretu's auto-reannounce cadence
            // (≈ TTL-based). Re-derives health every tick too, so a
            // newly-blocked state surfaces within ~10s of the change.
            _mdnsHealthTimer = new System.Threading.Timer(_ =>
            {
                try { _serviceDiscovery?.QueryServiceInstances(NormaliseServiceName(_settings.MdnsServiceType)); }
                catch { /* best effort */ }
                RecomputeMdnsHealth();
                RecomputeTcpHealth();
            }, null, dueTime: TimeSpan.FromSeconds(5), period: TimeSpan.FromSeconds(10));

            _logger.LogInformation("MdnsTcp: mDNS publisher started ({Service}, instance {Instance}, TCP {Port})",
                _settings.MdnsServiceType, _machineId, _settings.TcpPort);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MdnsTcp: mDNS publish/browse init failed; TCP listener still active for peers that find us another way");
            EmitMdnsHealth(TransportState.Blocked,
                $"Bonjour publisher failed to start: {ex.Message}");
        }

        // 3. Mirror UDP outgoing announcements onto every TCP peer.
        _disposables.Add(_udp.OutgoingAnnouncements.Subscribe(BroadcastToPeers));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        _disposables.Dispose();

        try { _mdnsHealthTimer?.Dispose(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _serviceDiscovery?.Dispose(); } catch { }
        try { _multicast?.Stop(); _multicast?.Dispose(); } catch { }

        foreach (var c in _peerClients.Values)
        {
            try { c.Close(); } catch { }
        }
        _peerClients.Clear();
        _cts.Dispose();
    }

    // ---------------------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            TcpClient peer;
            try
            {
                peer = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogDebug(ex, "MdnsTcp accept failed; retrying"); continue; }

            _logger.LogDebug("MdnsTcp: inbound TCP connection from {Endpoint}", peer.Client.RemoteEndPoint);
            _ = Task.Run(() => ReadLoopAsync(peer, ct));
        }
    }

    private async Task ReadLoopAsync(TcpClient peer, CancellationToken ct)
    {
        using (peer)
        {
            try
            {
                var stream = peer.GetStream();
                var lengthBuf = new byte[4];
                while (!ct.IsCancellationRequested)
                {
                    var got = await ReadExactAsync(stream, lengthBuf, ct).ConfigureAwait(false);
                    if (!got) return;
                    var len = BitConverter.ToInt32(lengthBuf, 0);
                    if (len <= 0 || len > 64 * 1024) return; // sanity cap (64KB)
                    var payload = new byte[len];
                    if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false)) return;

                    try
                    {
                        var announcement = JsonSerializer.Deserialize(payload,
                            ReceiverAnnouncementContext.Default.ReceiverAnnouncement);
                        if (announcement is not null)
                            _udp.InjectInbound(announcement, "tcp");
                    }
                    catch (JsonException) { /* skip malformed */ }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MdnsTcp read loop ended");
            }
        }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buf, CancellationToken ct)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await stream.ReadAsync(buf, read, buf.Length - read, ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private void OnServiceDiscovered(object? sender, DomainName serviceName)
    {
        // Got an answer that some service exists. Ask for its instances so
        // we can resolve peer endpoints.
        _serviceDiscovery?.QueryServiceInstances(serviceName);
    }

    private void OnInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs args)
    {
        try
        {
            var instance = args.ServiceInstanceName.ToString();
            // Filter to our service type to avoid acting on unrelated mDNS noise.
            if (!instance.Contains("_boltmate", StringComparison.OrdinalIgnoreCase)) return;
            // Self-echo path: when our OWN advertised instance comes back
            // to us, the discovery layer is alive end-to-end. Record the
            // timestamp + recompute health, then bail (we don't open a TCP
            // peer connection to ourselves).
            if (instance.StartsWith(_machineId, StringComparison.OrdinalIgnoreCase))
            {
                _lastSelfMdnsEcho = DateTimeOffset.UtcNow;
                RecomputeMdnsHealth();
                return;
            }

            // Extract host + port from the SRV record, and machineId TXT.
            IPEndPoint? endpoint = null;
            string? peerMachineId = null;
            foreach (var msg in args.Message.AdditionalRecords)
            {
                if (msg is SRVRecord srv)
                {
                    // Resolve the SRV target via A/AAAA in the same message.
                    foreach (var r in args.Message.AdditionalRecords)
                    {
                        if (r is ARecord a && string.Equals(a.Name.ToString(), srv.Target.ToString(), StringComparison.OrdinalIgnoreCase))
                            endpoint = new IPEndPoint(a.Address, srv.Port);
                    }
                }
                else if (msg is TXTRecord txt)
                {
                    foreach (var entry in txt.Strings)
                    {
                        var eq = entry.IndexOf('=');
                        if (eq < 0) continue;
                        var k = entry[..eq];
                        var v = entry[(eq + 1)..];
                        if (string.Equals(k, "machineId", StringComparison.OrdinalIgnoreCase))
                            peerMachineId = v;
                    }
                }
            }

            if (endpoint is null) return;
            if (string.IsNullOrEmpty(peerMachineId) || peerMachineId == _machineId) return;

            // Stamp the first time we observed a peer via Bonjour. After the
            // TcpConnectGrace window has elapsed without a successful
            // connect, TcpHealth flips to Blocked.
            if (_lastPeerDiscovered == default) _lastPeerDiscovered = DateTimeOffset.UtcNow;
            EnsureClientFor(peerMachineId, endpoint);
            RecomputeTcpHealth();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MdnsTcp: instance-discovery handler threw");
        }
    }

    private void EnsureClientFor(string peerMachineId, IPEndPoint endpoint)
    {
        // Already connected?
        if (_peerClients.TryGetValue(peerMachineId, out var existing) && existing.Connected) return;

        _ = Task.Run(async () =>
        {
            System.Threading.Interlocked.Increment(ref _tcpConnectAttempts);
            try
            {
                var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(endpoint.Address, endpoint.Port, _cts.Token).ConfigureAwait(false);
                _peerClients[peerMachineId] = client;
                _lastTcpConnectSuccess = DateTimeOffset.UtcNow;
                _logger.LogInformation("MdnsTcp: outbound TCP connected to peer {Machine} at {Endpoint}",
                    peerMachineId, endpoint);
                RecomputeTcpHealth();
            }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Increment(ref _tcpConnectFailures);
                _lastTcpFailureMessage = ex.Message;
                _logger.LogDebug(ex, "MdnsTcp: connect to peer {Machine} at {Endpoint} failed",
                    peerMachineId, endpoint);
                RecomputeTcpHealth();
            }
        });
    }

    private void BroadcastToPeers(ReceiverAnnouncement ann)
    {
        if (_peerClients.IsEmpty) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ann, ReceiverAnnouncementContext.Default.ReceiverAnnouncement);
        var prefix = BitConverter.GetBytes(bytes.Length);

        foreach (var (peerId, client) in _peerClients)
        {
            if (!client.Connected)
            {
                _peerClients.TryRemove(peerId, out _);
                continue;
            }
            try
            {
                var s = client.GetStream();
                s.Write(prefix, 0, 4);
                s.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MdnsTcp: write to peer {Machine} failed; will rediscover", peerId);
                try { client.Close(); } catch { }
                _peerClients.TryRemove(peerId, out _);
            }
        }
    }

    /// <summary>Normalises a service name to the form Makaretu expects (no trailing dot).</summary>
    private static string NormaliseServiceName(string s)
    {
        // Makaretu.Dns accepts either form; strip trailing dot for safety.
        return s.TrimEnd('.');
    }
}
