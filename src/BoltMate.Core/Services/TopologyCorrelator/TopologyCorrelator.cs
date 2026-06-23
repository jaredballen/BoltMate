using BoltMate.Core.Topology;
using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using BoltMate.Core.Bolt;

using DynamicData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.Core.Services;

/// <summary>
/// Cross-machine fan-out trigger. Filters inbound
/// <see cref="UdpTopologyService.Announcements"/> down to the device entries
/// that reference our host name in any slot of their slot map, then reacts
/// to <see cref="ReceiverAnnouncement.LastSwitchEvent"/> by asking
/// <see cref="SwitcherService.RequestTopologyFanOut"/> to switch our own
/// locally-connected devices to the same target host.
/// </summary>
/// <remarks>
/// Filtering happens at three nested levels and a surviving message is
/// re-emitted on <see cref="FilteredAnnouncements"/>:
/// <list type="bullet">
/// <item>Drop a <see cref="DeviceEntry"/> if no <see cref="DeviceSlotEntry"/>
///       reports our host name.</item>
/// <item>Drop a <see cref="ReceiverAnnouncementEntry"/> if it has no devices left.</item>
/// <item>Drop the whole message if it has no receivers left.</item>
/// </list>
/// The filter lives at the topology layer so the rest of the app only sees
/// messages that pertain to peripherals we share with the announcing peer.
/// </remarks>
public sealed class TopologyCorrelator : ITopologyCorrelator
{
    private readonly ReceiverManager _manager;
    private readonly ISwitcherService _switcher;
    private readonly IObservable<ReceiverAnnouncement> _announcements;
    private readonly IReadOnlyList<string> _localHostNames;
    private readonly ILogger<TopologyCorrelator> _logger;
    private readonly CompositeDisposable _disposables = new();
    private readonly System.Reactive.Subjects.Subject<ReceiverAnnouncement> _filtered = new();

    // Remote-reappearance tracking for hardware-switched (cycle button) devices
    private readonly ConcurrentDictionary<ushort, DateTimeOffset> _pendingLost = new();
    private static readonly TimeSpan ReappearanceWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Stream of announcements that passed the pruning filter — i.e. carry
    /// at least one device whose slot map references our host name. Surfaced
    /// for UI (peer-machine view) and tests.
    /// </summary>
    public IObservable<ReceiverAnnouncement> FilteredAnnouncements =>
        System.Reactive.Linq.Observable.AsObservable(_filtered);

    private readonly TimeProvider _time;

    public TopologyCorrelator(
        ReceiverManager manager,
        ISwitcherService switcher,
        IObservable<ReceiverAnnouncement> announcements,
        IReadOnlyList<string> localHostNames,
        ILogger<TopologyCorrelator>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _manager = manager;
        _switcher = switcher;
        _announcements = announcements;
        _localHostNames = localHostNames is { Count: > 0 } ? localHostNames : new[] { "unknown" };
        _logger = logger ?? NullLogger<TopologyCorrelator>.Instance;
        _time = timeProvider ?? TimeProvider.System;

        _disposables.Add(_announcements.Subscribe(OnAnnouncement));
        _disposables.Add(_filtered);

        // Subscribe to local LinkLost events to track hardware-switched devices
        _disposables.Add(_manager.Receivers.Connect()
            .MergeMany(r => r.LinkLost)
            .Subscribe(OnLocalLinkLost));
    }

    public void Dispose() => _disposables.Dispose();

    private void OnAnnouncement(ReceiverAnnouncement announcement)
    {
        _logger.LogInformation(
            "Topology: rx announcement from {Machine} (seq {Seq}, {Receivers} receivers, switchEvent={Sw})",
            announcement.Hostname, announcement.Seq, announcement.Receivers.Count,
            announcement.LastSwitchEvent is null ? "(none)" : $"{announcement.LastSwitchEvent.DeviceSerial ?? "?"}->{announcement.LastSwitchEvent.TargetHostName}");

        var pruned = Prune(announcement, _localHostNames);
        if (pruned is null)
        {
            var preDump = string.Join("; ", announcement.Receivers.SelectMany(r => r.Devices).Select(d =>
                $"slot{d.Slot} wpid={d.WpidHex} serial={d.Serial ?? "(null)"} linkUp={d.LinkUp} bindings=[" +
                string.Join(",", d.SlotMap.Select(s => $"h{s.HostIndex}:paired={s.Paired}|name='{s.HostName ?? "(null)"}'")) + "]"));
            _logger.LogInformation(
                "Topology: announcement from {Machine} pruned to EMPTY (localHosts=[{Local}]); pre-prune devices: [{Pre}]",
                announcement.Hostname, string.Join(",", _localHostNames), preDump);
            return;
        }

        var prunedDump = string.Join("; ", pruned.Receivers.SelectMany(r => r.Devices).Select(d =>
            $"slot{d.Slot} wpid={d.WpidHex} serial={d.Serial ?? "(null)"} linkUp={d.LinkUp} bindings=[" +
            string.Join(",", d.SlotMap.Select(s => $"h{s.HostIndex}:paired={s.Paired}|name='{s.HostName ?? "(null)"}'")) + "]"));
        _logger.LogInformation(
            "Topology: announcement from {Machine} POST-PRUNE devices: [{Dump}]",
            announcement.Hostname, prunedDump);

        _filtered.OnNext(pruned);

        // React to the explicit switch event. The broadcaster is telling us
        // "the user just sent a device to host X" — if X is not us, fan our
        // own locally-connected devices to X so the peripheral set follows.
        var ev = pruned.LastSwitchEvent;
        if (ev is not null)
        {
            if (string.IsNullOrWhiteSpace(ev.TargetHostName)) return;
            if (LocalHostNameMatches(ev.TargetHostName))
            {
                _logger.LogDebug("Topology: switch event targets us ({Target}) — no fan-out needed", ev.TargetHostName);
                return;
            }

            _logger.LogInformation(
                "Topology: switch event from {Machine} — device {Serial} headed to host '{Target}'. Fanning out local devices.",
                announcement.Hostname, ev.DeviceSerial ?? "?", ev.TargetHostName);

            var count = _switcher.RequestTopologyFanOut(
                ev.TargetHostName,
                originatingDeviceWpid: null,
                FanOutSource.RemoteTopology);
            _logger.LogInformation("Topology fan-out completed: {Count} sibling(s) switched", count);
        }
        else if (!_pendingLost.IsEmpty)
        {
            CheckRemoteReappearance(pruned);
        }
    }

    private void OnLocalLinkLost(PairedDevice device)
    {
        var wpid = device.Wpid;
        if (wpid == 0) return;
        _logger.LogInformation("Topology: local device link-lost observed for wpid 0x{Wpid:X4}", wpid);
        _pendingLost[wpid] = _time.GetUtcNow();
    }

    private void CheckRemoteReappearance(ReceiverAnnouncement pruned)
    {
        var now = _time.GetUtcNow();
        var pendingDump = string.Join(",", _pendingLost.Select(kv => $"0x{kv.Key:X4}@{(now-kv.Value).TotalSeconds:F1}s"));
        _logger.LogInformation(
            "Topology: CheckRemoteReappearance from {Machine} — pendingLost=[{Pending}], scanning {DevCount} pruned device(s)",
            pruned.Hostname, pendingDump, pruned.Receivers.Sum(r => r.Devices.Count));
        foreach (var receiver in pruned.Receivers)
        {
            foreach (var device in receiver.Devices)
            {
                if (!device.LinkUp)
                {
                    _logger.LogInformation("Reappearance scan: skip slot{Slot} wpid={Wpid} — LinkUp=false", device.Slot, device.WpidHex);
                    continue;
                }
                if (string.IsNullOrEmpty(device.WpidHex))
                {
                    _logger.LogInformation("Reappearance scan: skip slot{Slot} — WpidHex empty", device.Slot);
                    continue;
                }
                if (!ushort.TryParse(device.WpidHex, System.Globalization.NumberStyles.HexNumber, null, out var wpid))
                {
                    _logger.LogInformation("Reappearance scan: skip slot{Slot} wpid={Wpid} — unparseable", device.Slot, device.WpidHex);
                    continue;
                }

                _logger.LogInformation("Reappearance scan: slot{Slot} wpid=0x{Wpid:X4} pending={Pending}",
                    device.Slot, wpid, _pendingLost.ContainsKey(wpid));

                if (_pendingLost.TryGetValue(wpid, out var lostAt))
                {
                    if (now - lostAt <= ReappearanceWindow)
                    {
                        _logger.LogInformation(
                            "Topology: detected remote reappearance of device 0x{Wpid:X4} on host '{Machine}' within {Window}s of local link-lost. Fanning out remaining local devices.",
                            wpid, pruned.Hostname, (now - lostAt).TotalSeconds);

                        _pendingLost.TryRemove(wpid, out _);

                        var count = _switcher.RequestTopologyFanOut(
                            pruned.Hostname,
                            originatingDeviceWpid: wpid,
                            FanOutSource.RemoteTopology);
                        _logger.LogInformation("Topology remote-reappearance fan-out completed: {Count} sibling(s) switched", count);
                    }
                    else
                    {
                        _pendingLost.TryRemove(wpid, out _);
                    }
                }
            }
        }

        // Clean up expired items in _pendingLost
        foreach (var kv in _pendingLost)
        {
            if (now - kv.Value > ReappearanceWindow)
            {
                _pendingLost.TryRemove(kv.Key, out _);
            }
        }
    }

    /// <summary>
    /// Returns the announcement with devices pruned to those whose slot map
    /// references ANY of <paramref name="localHostNames"/>, receivers pruned
    /// to those with surviving devices, and null if nothing survives. The
    /// alias list covers the multiple identities a single machine can be
    /// known by (e.g. macOS friendly name + BSD short name).
    /// </summary>
    public static ReceiverAnnouncement? Prune(ReceiverAnnouncement source, IReadOnlyList<string> localHostNames)
    {
        if (localHostNames is null || localHostNames.Count == 0) return null;

        var prunedReceivers = new List<ReceiverAnnouncementEntry>();
        foreach (var receiver in source.Receivers)
        {
            var keptDevices = new List<DeviceEntry>();
            foreach (var device in receiver.Devices)
            {
                if (!ReferencesHost(device, localHostNames)) continue;
                keptDevices.Add(device);
            }
            if (keptDevices.Count == 0) continue;
            prunedReceivers.Add(new ReceiverAnnouncementEntry
            {
                Serial = receiver.Serial,
                Devices = keptDevices,
            });
        }
        if (prunedReceivers.Count == 0) return null;

        return new ReceiverAnnouncement
        {
            V = source.V,
            MachineId = source.MachineId,
            Hostname = source.Hostname,
            Timestamp = source.Timestamp,
            Seq = source.Seq,
            ReceiverCount = source.ReceiverCount,
            Receivers = prunedReceivers,
            LastSwitchEvent = source.LastSwitchEvent,
            Acks = source.Acks,
        };
    }

    private static bool ReferencesHost(DeviceEntry device, IReadOnlyList<string> localHostNames)
    {
        foreach (var slot in device.SlotMap)
        {
            if (!slot.Paired) continue;
            if (string.IsNullOrWhiteSpace(slot.HostName)) continue;
            foreach (var local in localHostNames)
                if (HostNameHelper.HostNameMatches(slot.HostName, local))
                    return true;
        }
        return false;
    }

    private bool LocalHostNameMatches(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        foreach (var local in _localHostNames)
            if (HostNameHelper.HostNameMatches(candidate, local))
                return true;
        return false;
    }
}
