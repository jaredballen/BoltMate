using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using DynamicData;
using LogiPlusSwitcher.Core.Bolt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogiPlusSwitcher.Core.Topology;

/// <summary>
/// LAN-only UDP broadcast + multicast announcement channel. Each announcement
/// carries a monotonic <see cref="ReceiverAnnouncement.Seq"/>; the same
/// announcement is sent N× back-to-back (<see cref="TopologySettings.RepeatCount"/>)
/// so a single dropped packet doesn't lose the message. Receivers dedup by
/// (machineId, seq).
/// </summary>
/// <remarks>
/// Cadence is dynamic. Normal interval is
/// <see cref="TopologySettings.BroadcastIntervalSeconds"/> (default 2s). When a
/// local device link-lost fires, we enter a burst window
/// (<see cref="TopologySettings.BurstDurationMs"/>, default 3s) where the
/// interval tightens to <see cref="TopologySettings.BurstIntervalMs"/> (200ms).
/// Peers' correlators are actively watching during exactly this window, so the
/// tighter cadence increases the chance our announcement lands.
/// </remarks>
public sealed class UdpTopologyService : IDisposable
{
    private readonly ReceiverManager _manager;
    private readonly TopologySettings _settings;
    private readonly string _machineId;
    private readonly string _hostname;
    private readonly ILogger<UdpTopologyService> _logger;
    private readonly Subject<ReceiverAnnouncement> _announcements = new();
    private readonly CompositeDisposable _disposables = new();
    // Per-peer last-seen sequence; suppresses N× repeats + late re-orderings.
    private readonly ConcurrentDictionary<string, ulong> _lastSeenSeq = new();
    // Per-peer observability: count of unique announcements + detected gaps in
    // their sequence numbers + wall-clock last-seen. Exposed to UI / diagnostics.
    private readonly ConcurrentDictionary<string, PeerStats> _peerStats = new();
    // Latest full announcement per peer — exposed to Settings so the hotkey
    // target dropdown can discover remote receiver BLEs even when our own
    // local devices haven't enriched yet.
    private readonly ConcurrentDictionary<string, ReceiverAnnouncement> _latestByPeer = new();
    // Send-side counters — increments for every attempt; errors are SocketExceptions.
    private long _sendAttempts;
    private long _sendErrors;
    private UdpClient? _socket;
    private CancellationTokenSource? _cts;
    private IPAddress? _multicastAddress;
    private long _nextSeq;                 // monotonic sequence counter
    private long _burstUntilTicks;         // DateTime.UtcNow.Ticks; <= now means not bursting
    private bool _disposed;

    /// <summary>Hot stream of remote-only announcements (own machineId + duplicate seqs filtered).</summary>
    public IObservable<ReceiverAnnouncement> Announcements => _announcements.AsObservable();

    /// <summary>Stable machine id for this host. Echoed in every outgoing announcement.</summary>
    public string MachineId => _machineId;

    /// <summary>Send-side counters: total attempts, total errors (SocketException etc).</summary>
    public (long Attempts, long Errors) SendStats =>
        (Interlocked.Read(ref _sendAttempts), Interlocked.Read(ref _sendErrors));

    /// <summary>Snapshot of per-peer observability for the diagnostics UI.</summary>
    public IReadOnlyCollection<PeerStats> PeerSnapshot => _peerStats.Values.ToArray();

    /// <summary>Latest full announcement received per peer — UI uses this to discover remote receiver BLEs.</summary>
    public IReadOnlyCollection<ReceiverAnnouncement> LatestPeerAnnouncements => _latestByPeer.Values.ToArray();

    public UdpTopologyService(
        ReceiverManager manager,
        TopologySettings settings,
        string machineId,
        ILogger<UdpTopologyService>? logger = null)
    {
        _manager = manager;
        _settings = settings;
        _machineId = machineId;
        _hostname = SafeHostname();
        _logger = logger ?? NullLogger<UdpTopologyService>.Instance;
        _disposables.Add(_announcements);
    }

    /// <summary>Opens the socket, starts the broadcast timer and the receive loop. Idempotent.</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UdpTopologyService));
        if (_socket is not null) return;

        try
        {
            _socket = new UdpClient(AddressFamily.InterNetwork);
            _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, _settings.Port));
            _socket.EnableBroadcast = true;

            if (_settings.UseMulticast && IPAddress.TryParse(_settings.MulticastGroup, out var mcast))
            {
                _multicastAddress = mcast;
                try
                {
                    _socket.JoinMulticastGroup(mcast);
                }
                catch (SocketException ex)
                {
                    _logger.LogWarning(ex, "JoinMulticastGroup({Group}) failed; multicast send only", mcast);
                }
            }
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "Topology UDP bind on port {Port} failed; topology disabled this session", _settings.Port);
            _socket?.Dispose();
            _socket = null;
            return;
        }

        _cts = new CancellationTokenSource();

        // Burst trigger: when any local device's link drops, schedule a tighter
        // cadence for BurstDurationMs. Peers correlate within their own window
        // — extra announcements here directly raise the hit rate.
        _disposables.Add(_manager.Receivers.Connect()
            .MergeMany(r => r.LinkLost)
            .Subscribe(d =>
            {
                Interlocked.Exchange(ref _burstUntilTicks,
                    DateTime.UtcNow.AddMilliseconds(_settings.BurstDurationMs).Ticks);
                _logger.LogDebug("Topology: entering burst window for {Ms}ms after slot {Slot} link-lost",
                    _settings.BurstDurationMs, d.DeviceIndex);
            }));

        _disposables.Add(Disposable.Create(() =>
        {
            try { _cts?.Cancel(); } catch { }
            try { _socket?.Close(); } catch { }
        }));

        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _ = Task.Run(() => BroadcastLoopAsync(_cts.Token));

        _logger.LogInformation(
            "UDP topology started: port {Port}, machineId {MachineId}, hostname {Hostname}, multicast={Mcast}, repeat={Repeat}",
            _settings.Port, _machineId, _hostname,
            _multicastAddress?.ToString() ?? "(off)", _settings.RepeatCount);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposables.Dispose();
        _socket?.Dispose();
        _cts?.Dispose();
    }

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _socket is not null)
        {
            try
            {
                var seq = (ulong)Interlocked.Increment(ref _nextSeq);
                var payload = BuildAnnouncement(seq);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ReceiverAnnouncementContext.Default.ReceiverAnnouncement);
                var endpoints = AllEndpoints();

                // N× repeats — same Seq each time. Peers dedup.
                for (var rep = 0; rep < Math.Max(1, _settings.RepeatCount); rep++)
                {
                    foreach (var endpoint in endpoints)
                    {
                        Interlocked.Increment(ref _sendAttempts);
                        try
                        {
                            await _socket.SendAsync(bytes, endpoint, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                        {
                            Interlocked.Increment(ref _sendErrors);
                            _logger.LogDebug(ex, "Topology send to {Endpoint} failed", endpoint);
                        }
                    }
                    if (rep < _settings.RepeatCount - 1 && _settings.RepeatGapMs > 0)
                    {
                        try { await Task.Delay(_settings.RepeatGapMs, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Topology broadcast tick failed");
            }

            // Cadence: tighter while inside a burst window, otherwise normal.
            var burstUntil = new DateTime(Interlocked.Read(ref _burstUntilTicks), DateTimeKind.Utc);
            var bursting = DateTime.UtcNow < burstUntil;
            var sleepMs = bursting ? _settings.BurstIntervalMs : _settings.BroadcastIntervalSeconds * 1000;
            try { await Task.Delay(Math.Max(50, sleepMs), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _socket is not null)
        {
            UdpReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Topology recv failed; will retry");
                continue;
            }

            try
            {
                var announcement = JsonSerializer.Deserialize(result.Buffer, ReceiverAnnouncementContext.Default.ReceiverAnnouncement);
                if (announcement is null) continue;
                if (announcement.MachineId == _machineId) continue; // own packet bouncing back

                // Dedup: ignore any seq <= last-seen for this peer.
                var key = announcement.MachineId;
                if (_lastSeenSeq.TryGetValue(key, out var lastSeq) && announcement.Seq <= lastSeq)
                {
                    // Duplicate (one of the N× repeats) — bump dedup counter.
                    if (_peerStats.TryGetValue(key, out var dupStats))
                    {
                        Interlocked.Increment(ref dupStats.DuplicatesSuppressed);
                        dupStats.LastSeenUtc = DateTime.UtcNow;
                    }
                    continue;
                }

                // Fresh announcement: detect gap (= packet loss) vs last seq we saw.
                _lastSeenSeq[key] = announcement.Seq;
                var stats = _peerStats.GetOrAdd(key, k => new PeerStats { MachineId = k });
                stats.Hostname = announcement.Hostname;
                stats.LastSeenUtc = DateTime.UtcNow;
                stats.LastSeq = announcement.Seq;
                if (lastSeq > 0 && announcement.Seq > lastSeq + 1)
                {
                    var missed = announcement.Seq - lastSeq - 1;
                    Interlocked.Add(ref stats.MissedFromPeer, (long)missed);
                    _logger.LogDebug("Topology: gap from peer {Machine} — missed {N} announcement(s) ({Last} -> {Now})",
                        key, missed, lastSeq, announcement.Seq);
                }
                Interlocked.Increment(ref stats.UniqueReceived);

                // Cache the full payload so Settings → Hotkeys can discover
                // remote receiver BLEs even when our own HostBindings are empty.
                _latestByPeer[key] = announcement;

                // Mutual ack — peer told us the last Seq of OURS they got.
                // We track that so the diagnostics view can show outbound loss.
                foreach (var ack in announcement.Acks)
                {
                    if (ack.MachineId == _machineId)
                    {
                        stats.LastAckOfOurSeq = ack.LastSeq;
                        var ourLatest = (ulong)Interlocked.Read(ref _nextSeq);
                        stats.OutboundLossEstimate = ourLatest > ack.LastSeq ? (long)(ourLatest - ack.LastSeq) : 0;
                        break;
                    }
                }

                _announcements.OnNext(announcement);
            }
            catch (JsonException)
            {
                // Foreign UDP traffic on the same port — ignore quietly.
            }
        }
    }

    private ReceiverAnnouncement BuildAnnouncement(ulong seq)
    {
        var ann = new ReceiverAnnouncement
        {
            MachineId = _machineId,
            Hostname = _hostname,
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            Seq = seq,
        };
        // Mutual ack — echo the last Seq we saw from every known peer so they
        // can measure their outbound packet loss to us.
        foreach (var (peerId, lastSeq) in _lastSeenSeq)
            ann.Acks.Add(new PeerAck { MachineId = peerId, LastSeq = lastSeq });
        foreach (var receiver in _manager.Receivers.Items)
        {
            var entry = new ReceiverAnnouncementEntry
            {
                Serial = receiver.Info.Serial,
                BluetoothAddressHex = receiver.BluetoothAddressKey,
            };
            foreach (var device in receiver.Devices.Items)
            {
                if (!device.LinkUp) continue;
                entry.OnlineDevices.Add(new OnlineDeviceEntry
                {
                    Slot = device.DeviceIndex,
                    WpidHex = device.Wpid.ToString("X4"),
                    Name = device.DisplayName,
                });
            }
            ann.Receivers.Add(entry);
        }
        return ann;
    }

    /// <summary>Per-interface broadcast endpoints + the multicast group endpoint.</summary>
    private IEnumerable<IPEndPoint> AllEndpoints()
    {
        var port = _settings.Port;
        var found = new List<IPEndPoint>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = ni.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (addr.IPv4Mask is null) continue;
                    var broadcast = ComputeBroadcast(addr.Address, addr.IPv4Mask);
                    if (broadcast is not null) found.Add(new IPEndPoint(broadcast, port));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Topology interface enumeration failed; falling back to 255.255.255.255");
        }

        if (found.Count == 0)
            found.Add(new IPEndPoint(IPAddress.Broadcast, port));

        if (_multicastAddress is not null)
            found.Add(new IPEndPoint(_multicastAddress, port));

        return found;
    }

    private static IPAddress? ComputeBroadcast(IPAddress addr, IPAddress mask)
    {
        var a = addr.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (a.Length != 4 || m.Length != 4) return null;
        var b = new byte[4];
        for (var i = 0; i < 4; i++) b[i] = (byte)(a[i] | (~m[i] & 0xFF));
        return new IPAddress(b);
    }

    private static string SafeHostname()
    {
        try { return Dns.GetHostName(); }
        catch { return "unknown"; }
    }
}

/// <summary>
/// Observability snapshot per remote machine — what we've heard from them,
/// how much we missed in flight, when they last spoke. Surfaced in the
/// Settings -> Network diagnostics panel and useful for spotting flaky links.
/// </summary>
public sealed class PeerStats
{
    public string MachineId { get; set; } = "";
    public string? Hostname { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public ulong LastSeq { get; set; }

    /// <summary>Count of unique announcements received (deduped repeats not counted).</summary>
    public long UniqueReceived;

    /// <summary>How many of THEIR announcements we missed (detected via Seq gaps).</summary>
    public long MissedFromPeer;

    /// <summary>How many duplicate packets (their N× repeats) we suppressed.</summary>
    public long DuplicatesSuppressed;

    /// <summary>
    /// Most recent ack of OUR seq the peer has echoed back. If our latest
    /// outgoing seq is N and this is N-K, K of our announcements never
    /// reached this peer.
    /// </summary>
    public ulong LastAckOfOurSeq;

    /// <summary>Inferred us→peer packet loss (our seq minus peer ack at last receive).</summary>
    public long OutboundLossEstimate;
}

