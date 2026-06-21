using System.Reactive.Disposables;
using BoltMate.Core.Bolt;
using BoltMate.Core.Switcher;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.Core.Topology;

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
public sealed class TopologyCorrelator : IDisposable
{
    private readonly ReceiverManager _manager;
    private readonly SwitcherService _switcher;
    private readonly IObservable<ReceiverAnnouncement> _announcements;
    private readonly string _localHostName;
    private readonly ILogger<TopologyCorrelator> _logger;
    private readonly CompositeDisposable _disposables = new();
    private readonly System.Reactive.Subjects.Subject<ReceiverAnnouncement> _filtered = new();

    /// <summary>
    /// Stream of announcements that passed the pruning filter — i.e. carry
    /// at least one device whose slot map references our host name. Surfaced
    /// for UI (peer-machine view) and tests.
    /// </summary>
    public IObservable<ReceiverAnnouncement> FilteredAnnouncements =>
        System.Reactive.Linq.Observable.AsObservable(_filtered);

    public TopologyCorrelator(
        ReceiverManager manager,
        SwitcherService switcher,
        IObservable<ReceiverAnnouncement> announcements,
        string localHostName,
        ILogger<TopologyCorrelator>? logger = null)
    {
        _manager = manager;
        _switcher = switcher;
        _announcements = announcements;
        _localHostName = localHostName;
        _logger = logger ?? NullLogger<TopologyCorrelator>.Instance;

        _disposables.Add(_announcements.Subscribe(OnAnnouncement));
        _disposables.Add(_filtered);
    }

    public void Dispose() => _disposables.Dispose();

    private void OnAnnouncement(ReceiverAnnouncement announcement)
    {
        _logger.LogInformation(
            "Topology: rx announcement from {Machine} (seq {Seq}, {Receivers} receivers, switchEvent={Sw})",
            announcement.Hostname, announcement.Seq, announcement.Receivers.Count,
            announcement.LastSwitchEvent is null ? "(none)" : $"{announcement.LastSwitchEvent.DeviceSerial ?? "?"}->{announcement.LastSwitchEvent.TargetHostName}");

        var pruned = Prune(announcement, _localHostName);
        if (pruned is null)
        {
            _logger.LogDebug("Topology: announcement from {Machine} pruned to empty — dropping", announcement.Hostname);
            return;
        }

        _filtered.OnNext(pruned);

        // React to the explicit switch event. The broadcaster is telling us
        // "the user just sent a device to host X" — if X is not us, fan our
        // own locally-connected devices to X so the peripheral set follows.
        var ev = pruned.LastSwitchEvent;
        if (ev is null) return;
        if (string.IsNullOrWhiteSpace(ev.TargetHostName)) return;
        if (string.Equals(ev.TargetHostName, _localHostName, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Returns the announcement with devices pruned to those whose slot map
    /// references <paramref name="localHostName"/>, receivers pruned to those
    /// with surviving devices, and null if nothing survives.
    /// </summary>
    public static ReceiverAnnouncement? Prune(ReceiverAnnouncement source, string localHostName)
    {
        if (string.IsNullOrWhiteSpace(localHostName)) return null;

        var prunedReceivers = new List<ReceiverAnnouncementEntry>();
        foreach (var receiver in source.Receivers)
        {
            var keptDevices = new List<DeviceEntry>();
            foreach (var device in receiver.Devices)
            {
                if (!ReferencesHost(device, localHostName)) continue;
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

    private static bool ReferencesHost(DeviceEntry device, string hostName)
    {
        foreach (var slot in device.SlotMap)
        {
            if (!slot.Paired) continue;
            if (string.IsNullOrWhiteSpace(slot.HostName)) continue;
            if (string.Equals(slot.HostName, hostName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
