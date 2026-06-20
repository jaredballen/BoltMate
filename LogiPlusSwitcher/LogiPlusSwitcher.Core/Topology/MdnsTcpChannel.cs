using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Disposables;
using System.Text.Json;
using Makaretu.Dns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogiPlusSwitcher.Core.Topology;

/// <summary>
/// Parallel-to-UDP transport for topology announcements. Uses mDNS to
/// discover peers (publishes <c>_logiplus._udp.local</c> with a TXT record
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
    }

    /// <summary>Idempotently starts the mDNS publisher, browser, and TCP listener.</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MdnsTcpChannel));
        if (_listener is not null) return;

        // 1. TCP listener — peers will connect to us once they've discovered
        //    us via mDNS. We accept and read length-prefixed JSON.
        try
        {
            _listener = new TcpListener(IPAddress.Any, _settings.TcpPort);
            _listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _logger.LogInformation("MdnsTcp: listening on TCP port {Port}", _settings.TcpPort);
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "MdnsTcp: TCP bind on port {Port} failed; mDNS+TCP transport disabled this session",
                _settings.TcpPort);
            _listener = null;
            return;
        }

        // 2. mDNS publisher + browser. Makaretu.Dns.Multicast handles
        //    Bonjour-compatible service announcements + browsing.
        try
        {
            _multicast = new MulticastService();
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
            // Query immediately so we don't wait the full TTL.
            _serviceDiscovery.QueryServiceInstances(NormaliseServiceName(_settings.MdnsServiceType));

            _logger.LogInformation("MdnsTcp: mDNS publisher started ({Service}, instance {Instance}, TCP {Port})",
                _settings.MdnsServiceType, _machineId, _settings.TcpPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MdnsTcp: mDNS init failed; TCP listener still active for peers that find us another way");
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
            if (!instance.Contains("_logiplus", StringComparison.OrdinalIgnoreCase)) return;
            // Skip our own instance.
            if (instance.StartsWith(_machineId, StringComparison.OrdinalIgnoreCase)) return;

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

            EnsureClientFor(peerMachineId, endpoint);
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
            try
            {
                var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(endpoint.Address, endpoint.Port, _cts.Token).ConfigureAwait(false);
                _peerClients[peerMachineId] = client;
                _logger.LogInformation("MdnsTcp: outbound TCP connected to peer {Machine} at {Endpoint}",
                    peerMachineId, endpoint);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MdnsTcp: connect to peer {Machine} at {Endpoint} failed",
                    peerMachineId, endpoint);
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
