using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using DynamicData;
using BoltMate.Core.Bolt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.Core.Topology;

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
    private readonly Subject<ReceiverAnnouncement> _outgoing = new();
    private readonly CompositeDisposable _disposables = new();
    // Per-peer last-seen sequence; suppresses N× repeats + late re-orderings.
    private readonly ConcurrentDictionary<string, ulong> _lastSeenSeq = new();
    // Per-peer observability: count of unique announcements + detected gaps in
    // their sequence numbers + wall-clock last-seen. Exposed to UI / diagnostics.
    private readonly ConcurrentDictionary<string, PeerStats> _peerStats = new();
    // Latest full announcement per peer — exposed to Settings so the Status
    // tab can show peer receivers and devices even when our own local devices
    // haven't enriched yet.
    private readonly ConcurrentDictionary<string, ReceiverAnnouncement> _latestByPeer = new();
    // Send-side counters — increments for every attempt; errors are SocketExceptions.
    private long _sendAttempts;
    private long _sendErrors;
    private UdpClient? _socket;
    private CancellationTokenSource? _cts;
    private IPAddress? _multicastAddress;
    private long _nextSeq;                 // monotonic sequence counter
    private long _burstUntilTicks;         // DateTime.UtcNow.Ticks; <= now means not bursting
    private readonly ManualResetEventSlim _kickBroadcast = new(false);  // wakes the broadcast loop early
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

    /// <summary>
    /// Fires every time we send an announcement. Auxiliary channels (mDNS+TCP)
    /// subscribe to mirror the same payload over their transport.
    /// </summary>
    public IObservable<ReceiverAnnouncement> OutgoingAnnouncements => _outgoing.AsObservable();

    /// <summary>
    /// Push an inbound announcement received via an auxiliary channel (e.g.
    /// TCP). Runs the same dedup + stats + emit pipeline as UDP inbound.
    /// </summary>
    public void InjectInbound(ReceiverAnnouncement announcement, string channel = "ext") =>
        HandleInbound(announcement, channel);

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

        // Burst + kick triggers: any local link-state change is important
        // enough to broadcast about NOW. Local link-LOST → our correlator is
        // about to start watching; tight cadence helps. Local link-UP → some
        // peer's correlator is watching for THIS device; we need to tell
        // them ASAP. Both fire an immediate kick PLUS a burst window.
        _disposables.Add(_manager.Receivers.Connect()
            .MergeMany(r => r.LinkLost)
            .Subscribe(d => TriggerImmediateBroadcast(d.DeviceIndex, "link-lost")));
        _disposables.Add(_manager.Receivers.Connect()
            .MergeMany(r => r.LinkEstablished)
            .Subscribe(d => TriggerImmediateBroadcast(d.DeviceIndex, "link-up")));

        // Also re-broadcast after device metadata refresh (HostBindings,
        // CurrentHost, name) so the per-device data in the announcement
        // updates as soon as enrichment completes — critical when a device
        // arrives and its HostBindings aren't read yet at link-UP time.
        _disposables.Add(_manager.Receivers.Connect()
            .MergeMany(r => r.Devices.Connect().WhereReasonsAre(DynamicData.ChangeReason.Refresh))
            .Sample(TimeSpan.FromMilliseconds(250))    // coalesce enrichment bursts
            .Subscribe(_ => TriggerImmediateBroadcast(0, "device-refresh")));

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
        _kickBroadcast.Dispose();
    }

    /// <summary>
    /// Schedules a burst window + wakes the broadcast loop NOW so the next
    /// announcement lands within milliseconds rather than waiting for the
    /// regular tick. Critical for cross-machine correlation — a peer's
    /// correlator only watches for 3s after a link event, so we need to
    /// reach them inside that window when something happens locally.
    /// </summary>
    private void TriggerImmediateBroadcast(byte slot, string cause)
    {
        Interlocked.Exchange(ref _burstUntilTicks,
            DateTime.UtcNow.AddMilliseconds(_settings.BurstDurationMs).Ticks);
        _logger.LogDebug("Topology: kicking immediate broadcast (slot {Slot} {Cause}); burst for {Ms}ms",
            slot, cause, _settings.BurstDurationMs);
        _kickBroadcast.Set();
    }

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _socket is not null)
        {
            try
            {
                // No receivers → nothing for peers to act on. Skip the
                // tick entirely so a coordinator-only machine doesn't
                // generate LAN chatter with empty payloads.
                if (_manager.Receivers.Count == 0)
                {
                    goto sleep;
                }

                var seq = (ulong)Interlocked.Increment(ref _nextSeq);
                var payload = BuildAnnouncement(seq);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ReceiverAnnouncementContext.Default.ReceiverAnnouncement);
                try { _outgoing.OnNext(payload); } catch { /* observers must not block sends */ }
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

            sleep:
            // Cadence: tighter while inside a burst window, otherwise normal.
            // BUT — wake immediately if a local link event fires (kick event),
            // even mid-sleep. That gets our news to peers' correlators inside
            // their 3s watch window when they need to know NOW.
            var burstUntil = new DateTime(Interlocked.Read(ref _burstUntilTicks), DateTimeKind.Utc);
            var bursting = DateTime.UtcNow < burstUntil;
            var sleepMs = bursting ? _settings.BurstIntervalMs : _settings.BroadcastIntervalSeconds * 1000;
            try
            {
                // Wait either for the sleep to elapse OR for a kick.
                var waited = await Task.Run(() => _kickBroadcast.Wait(Math.Max(50, sleepMs), ct), ct).ConfigureAwait(false);
                if (waited) _kickBroadcast.Reset();
            }
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
                HandleInbound(announcement, "udp");
            }
            catch (JsonException)
            {
                // Foreign UDP traffic on the same port — ignore quietly.
            }
        }
    }

    private void HandleInbound(ReceiverAnnouncement announcement, string channel)
    {
        if (announcement.MachineId == _machineId) return; // own packet bouncing back

        var key = announcement.MachineId;
        if (_lastSeenSeq.TryGetValue(key, out var lastSeq) && announcement.Seq <= lastSeq)
        {
            // Peer-restart detection: a peer that restarts resets its seq to 1
            // but keeps its machineId. Our cached lastSeq would suppress every
            // post-restart packet forever. If the incoming seq is FAR below
            // the cached value (we'd need a huge multi-thousand-packet gap for
            // a legitimate replay to look like this), assume restart and let
            // the new run repopulate.
            if (announcement.Seq < lastSeq && (lastSeq - announcement.Seq) >= 100)
            {
                _logger.LogInformation(
                    "Topology({Ch}): peer {Machine} appears to have restarted (incoming seq {Now} far below cached {Last}); resetting dedup baseline",
                    channel, key, announcement.Seq, lastSeq);
                _lastSeenSeq.TryRemove(key, out _);
                // fall through — treat this as the first packet from the new run
            }
            else
            {
                // Duplicate (N× repeat OR same announcement from another channel).
                if (_peerStats.TryGetValue(key, out var dupStats))
                {
                    Interlocked.Increment(ref dupStats.DuplicatesSuppressed);
                    dupStats.LastSeenUtc = DateTime.UtcNow;
                }
                return;
            }
        }

        _lastSeenSeq[key] = announcement.Seq;
        var stats = _peerStats.GetOrAdd(key, k => new PeerStats { MachineId = k });
        stats.Hostname = announcement.Hostname;
        stats.LastSeenUtc = DateTime.UtcNow;
        stats.LastSeq = announcement.Seq;
        if (lastSeq > 0 && announcement.Seq > lastSeq + 1)
        {
            var missed = announcement.Seq - lastSeq - 1;
            Interlocked.Add(ref stats.MissedFromPeer, (long)missed);
            _logger.LogDebug("Topology({Ch}): gap from peer {Machine} — missed {N} ({Last} -> {Now})",
                channel, key, missed, lastSeq, announcement.Seq);
        }
        Interlocked.Increment(ref stats.UniqueReceived);
        _latestByPeer[key] = announcement;

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

    private ReceiverAnnouncement BuildAnnouncement(ulong seq)
    {
        var receivers = _manager.Receivers.Items.ToList();
        var ann = new ReceiverAnnouncement
        {
            MachineId = _machineId,
            Hostname = _hostname,
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            Seq = seq,
            ReceiverCount = receivers.Count,
            LastSwitchEvent = ConsumePendingSwitchEvent(),
        };
        // Mutual ack — echo the last Seq we saw from every known peer so they
        // can measure their outbound packet loss to us.
        foreach (var (peerId, lastSeq) in _lastSeenSeq)
            ann.Acks.Add(new PeerAck { MachineId = peerId, LastSeq = lastSeq });
        // BroadcastLoopAsync guarantees receivers.Count > 0 by this point —
        // we don't ship empty announcements (no signal for peers to act on).
        foreach (var receiver in receivers)
        {
            var entry = new ReceiverAnnouncementEntry
            {
                Serial = receiver.Info.Serial,
            };
            // Include every paired device — link state goes in the entry.
            // Peers prune on host-name match, not on link state.
            foreach (var device in receiver.Devices.Items)
            {
                var entryDevice = new DeviceEntry
                {
                    Slot = device.DeviceIndex,
                    WpidHex = device.Wpid.ToString("X4"),
                    Serial = device.Serial,
                    Name = device.DisplayName,
                    LinkUp = device.LinkUp,
                    CurrentHost = device.LastKnownCurrentHost,
                };
                foreach (var (hostIdx, binding) in device.HostBindings)
                {
                    entryDevice.SlotMap.Add(new DeviceSlotEntry
                    {
                        HostIndex = hostIdx,
                        Paired = binding.Paired,
                        HostName = binding.ReceiverName,
                    });
                }
                if (device.LastKnownBattery is { } bat)
                {
                    entryDevice.Battery = new BatteryEntry
                    {
                        Percent = bat.Percent,
                        State = (byte)bat.State,
                        ExternalPower = bat.ExternalPower,
                        Level = bat.Level.HasValue ? (byte)bat.Level.Value : null,
                    };
                }
                entry.Devices.Add(entryDevice);
            }
            ann.Receivers.Add(entry);
        }
        return ann;
    }

    private SwitchEvent? ConsumePendingSwitchEvent()
    {
        // Pulled once into the next outbound announcement. Subsequent
        // broadcasts will carry null until the next local switch fires.
        return System.Threading.Interlocked.Exchange(ref _pendingSwitchEvent, null);
    }

    /// <summary>
    /// Records a local host-switch event so the next outbound announcement
    /// surfaces it as <see cref="ReceiverAnnouncement.LastSwitchEvent"/>.
    /// Called by the App layer on local Easy-Switch press / Flow snoop.
    /// </summary>
    public void RecordLocalSwitchEvent(string? deviceSerial, string targetHostName)
    {
        System.Threading.Interlocked.Exchange(ref _pendingSwitchEvent, new SwitchEvent
        {
            DeviceSerial = deviceSerial,
            TargetHostName = targetHostName,
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
    }

    private SwitchEvent? _pendingSwitchEvent;

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

