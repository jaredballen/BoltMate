using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using BoltMate.Core.Bolt;
using BoltMate.Core.Switcher;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.Core.Topology;

/// <summary>
/// Cross-machine fan-out trigger. Watches local device link-lost events,
/// then watches inbound <see cref="UdpTopologyService.Announcements"/> for
/// the same WPID showing up on a remote machine within the correlation
/// window. On hit, asks <see cref="SwitcherService.RequestTopologyFanOut"/>
/// to fan the remaining local devices to the remote receiver's BLE.
/// </summary>
/// <remarks>
/// This is the "hybrid 1+3" path the user asked for — works when both ends
/// run this app and are on the same LAN. The physical Easy-Switch button
/// becomes a usable trigger again, because we re-derive the target from the
/// remote machine's announcement instead of trying to intercept the button.
/// </remarks>
public sealed class TopologyCorrelator : IDisposable
{
    private readonly ReceiverManager _manager;
    private readonly SwitcherService _switcher;
    private readonly IObservable<ReceiverAnnouncement> _announcements;
    private readonly TimeSpan _window;
    private readonly ILogger<TopologyCorrelator> _logger;
    private readonly CompositeDisposable _disposables = new();
    private readonly ConcurrentDictionary<ushort, DateTimeOffset> _pendingLost = new();

    public TopologyCorrelator(
        ReceiverManager manager,
        SwitcherService switcher,
        IObservable<ReceiverAnnouncement> announcements,
        TimeSpan correlationWindow,
        ILogger<TopologyCorrelator>? logger = null)
    {
        _manager = manager;
        _switcher = switcher;
        _announcements = announcements;
        _window = correlationWindow;
        _logger = logger ?? NullLogger<TopologyCorrelator>.Instance;

        // Index local link-lost across every attached receiver.
        _disposables.Add(_manager.Receivers.Connect()
            .MergeMany(r => r.LinkLost)
            .Subscribe(OnLinkLost));

        // Inbound announcements — match against pending-lost devices.
        _disposables.Add(_announcements.Subscribe(OnAnnouncement));
    }

    public void Dispose() => _disposables.Dispose();

    private void OnLinkLost(PairedDevice device)
    {
        if (device.Wpid == 0) return;
        _pendingLost[device.Wpid] = DateTimeOffset.UtcNow;
        _logger.LogInformation(
            "Topology: tracking link-lost for wpid {Wpid} (slot {Slot}, {Name}) — watching announcements for {Window}s",
            device.Wpid.ToString("X4"), device.DeviceIndex, device.DisplayName, _window.TotalSeconds);

        // Sweep expired entries so the dictionary doesn't grow.
        var cutoff = DateTimeOffset.UtcNow.Add(-_window);
        foreach (var kv in _pendingLost)
        {
            if (kv.Value < cutoff)
                _pendingLost.TryRemove(kv.Key, out _);
        }
    }

    private void OnAnnouncement(ReceiverAnnouncement announcement)
    {
        // Always log inbound — diagnostics-grade visibility into why a
        // pending-lost wpid does or doesn't match.
        _logger.LogInformation(
            "Topology: rx announcement from {Machine} (seq {Seq}, {Receivers} receivers, pendingLost={Pending})",
            announcement.Hostname, announcement.Seq, announcement.Receivers.Count, _pendingLost.Count);

        if (_pendingLost.IsEmpty) return;

        foreach (var receiver in announcement.Receivers)
        {
            foreach (var dev in receiver.OnlineDevices)
            {
                if (string.IsNullOrEmpty(dev.WpidHex)) continue;
                if (!ushort.TryParse(dev.WpidHex,
                        System.Globalization.NumberStyles.HexNumber,
                        null, out var wpid)) continue;

                if (!_pendingLost.TryGetValue(wpid, out var lostAt)) continue;
                if (DateTimeOffset.UtcNow - lostAt > _window) { _pendingLost.TryRemove(wpid, out _); continue; }

                // Cross-receiver matching is layered:
                //   1) Per-pairing identifier (works when sibling pairings
                //      share the destination's per-pair token — i.e. all
                //      devices paired in the same Logi+ session).
                //   2) Receiver friendly name (Logi+ writes the OS hostname
                //      into each device's host slot at pairing — stable
                //      across re-pair sessions because the hostname doesn't
                //      change). Used as fallback when (1) misses.
                // We collect BOTH from the local originator so the fan-out
                // can use whichever matches each sibling.
                string? destinationReceiverName = null;
                // Device-side resolution of the target identifier. Each
                // receiver writes its own identifier into the device's slot
                // table at pairing time — so the IDENTIFIER VALUES STORED ON
                // A DEVICE DIFFER PER RECEIVER (i.e. mouse paired separately
                // from keyboard ends up with different per-host values even
                // though they sit on the same physical Mac+Win receivers).
                //
                // To get an identifier siblings will recognize, we use the
                // LOCAL originator's view: the device that just left us is
                // still cached in this BoltReceiver with its HostBindings
                // populated. Index those bindings by the REMOTE-announced
                // CurrentHost — that's the destination slot the originator
                // moved to, and our local read of that slot's identifier is
                // the value our siblings have for the same destination
                // (because we wrote it into them at OUR pairing time).
                string? targetIdentifier = null;
                if (dev.CurrentHost is { } cur)
                {
                    // Find local originator across attached receivers. Re-pair
                    // operations leave stale wpid entries on the receiver
                    // (slots where the device was previously paired but never
                    // unpaired cleanly). Those entries lack populated
                    // HostBindings and would short-circuit our lookup. Prefer
                    // an entry that has bindings; fall back to any if none do.
                    PairedDevice? localOrig = null;
                    PairedDevice? localOrigFallback = null;
                    foreach (var rcv in _manager.Receivers.Items)
                    {
                        foreach (var d in rcv.Devices.Items)
                        {
                            if (d.Wpid != wpid) continue;
                            localOrigFallback ??= d;
                            if (d.HostBindings.Count > 0) { localOrig = d; break; }
                        }
                        if (localOrig is not null) break;
                    }
                    localOrig ??= localOrigFallback;
                    if (localOrig is not null &&
                        localOrig.HostBindings.TryGetValue(cur, out var localBinding))
                    {
                        if (localBinding.HostIdentifier is { Length: 6 } id)
                            targetIdentifier = Convert.ToHexString(id).ToLowerInvariant();
                        destinationReceiverName ??= localBinding.ReceiverName;
                    }

                    // Fall back to the remote announcement's view if local
                    // originator HostBindings aren't populated yet.
                    foreach (var b in dev.HostBindings)
                    {
                        if (b.HostIndex != cur) continue;
                        if (string.IsNullOrEmpty(targetIdentifier) && !string.IsNullOrEmpty(b.IdentifierHex))
                            targetIdentifier = b.IdentifierHex.ToLowerInvariant();
                        destinationReceiverName ??= b.ReceiverName;
                        break;
                    }
                }
                targetIdentifier ??= receiver.HostIdentifierHex?.ToLowerInvariant();
                if (string.IsNullOrEmpty(targetIdentifier))
                {
                    _logger.LogWarning(
                        "Topology: wpid {Wpid} matched in announcement from {Machine} but no target identifier resolvable (device.CurrentHost={Ch}, device.HostBindings.Count={Bc}, receiver.HostIdentifierHex={Ri}) — fan-out skipped",
                        wpid.ToString("X4"), announcement.Hostname,
                        dev.CurrentHost, dev.HostBindings.Count, receiver.HostIdentifierHex);
                    continue;
                }

                // Match! Device that just left us has appeared on the remote
                // machine's receiver. Fan out remaining local devices.
                _pendingLost.TryRemove(wpid, out _);

                _logger.LogInformation(
                    "Topology MATCH: wpid {Wpid} ({Name}) reappeared on remote {Machine} — target identifier {Id}, name fallback '{HostName}' — fanning out siblings",
                    wpid.ToString("X4"), dev.Name, announcement.Hostname, targetIdentifier, destinationReceiverName ?? "(none)");

                var count = _switcher.RequestTopologyFanOut(targetIdentifier, wpid, FanOutSource.RemoteTopology, destinationReceiverName);
                _logger.LogInformation("Topology fan-out completed: {Count} sibling(s) switched", count);
            }
        }
    }
}
