using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;
using BoltMate.Core.Bolt;
using BoltMate.Core.HidPp.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.Core.Services;

/// <summary>
/// Multi-receiver, topology-aware fan-out orchestrator. One instance per
/// <see cref="ReceiverManager"/> (NOT per receiver). Subscribes to every
/// attached <see cref="BoltReceiver"/>'s host-switch streams and routes the
/// trigger across all participating receivers, matching each sibling's
/// <see cref="PairedDevice.HostBindings"/> entry whose host friendly name
/// equals the target.
/// </summary>
/// <remarks>
/// Algorithm (per trigger event):
/// <list type="number">
/// <item>Resolve the originating device's target host name from its HostBindings.</item>
/// <item>For each sibling on any participating receiver, find the slot whose
/// binding's host name matches; CHANGE_HOST to that slot.</item>
/// <item>Skip the originator. Skip non-participating receivers. Skip siblings
/// without a matching binding (logged for UI hint).</item>
/// </list>
/// No identifier fallback: per-pairing host identifiers rotate when a device
/// is re-paired (verified empirically), so they're an unreliable correlation
/// key. The host friendly name (= system hostname at pairing time) survives
/// re-pair sessions and is what Logi+ surfaces in the channel picker.
/// </remarks>
public sealed class SwitcherService : ISwitcherService
{
    private readonly ReceiverManager _manager;
    private readonly ILogger<SwitcherService> _logger;
    private readonly Subject<FanOutEvent> _fanOuts = new();
    private readonly Subject<LocalSwitchTrigger> _triggers = new();
    private readonly CompositeDisposable _disposables = new();

    /// <summary>Stream of fan-out writes issued. One per sibling per trigger.</summary>
    public IObservable<FanOutEvent> FanOuts => _fanOuts.AsObservable();

    /// <summary>
    /// Stream of resolved local switch triggers. Fires once per Easy-Switch
    /// press / Flow snoop after the target hostname has been resolved from the
    /// originator's HostBindings, BEFORE local fan-out runs. Fires regardless
    /// of whether the local machine has any siblings to fan — the topology
    /// layer needs this signal even when the originator is the only local
    /// device, so peer machines can fan their own siblings.
    /// </summary>
    public IObservable<LocalSwitchTrigger> LocalSwitchTriggers => _triggers.AsObservable();

    public SwitcherService(ReceiverManager manager, ILogger<SwitcherService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
        _logger = logger ?? NullLogger<SwitcherService>.Instance;

        // MergeMany over each receiver's press/flow streams. The closure
        // captures the receiver so we know the origin without bookkeeping.
        _disposables.Add(_manager.Receivers.Connect()
            .MergeMany(r => r.HostSwitchPresses.Select(press => (Origin: r, Press: press)))
            .Subscribe(t => OnHostSwitchPressed(t.Origin, t.Press)));

        _disposables.Add(_manager.Receivers.Connect()
            .MergeMany(r => r.FlowHostSwitches.Select(snoop => (Origin: r, Snoop: snoop)))
            .Subscribe(t => OnFlowHostSwitchDetected(t.Origin, t.Snoop)));

        _disposables.Add(_fanOuts);
        _disposables.Add(_triggers);
    }

    public void Dispose() => _disposables.Dispose();

    /// <summary>
    /// Topology-aware fan-out by destination host name. Used by the UDP
    /// topology correlator (originator = the device that just left this
    /// machine) and by CLI / API callers requesting a user-initiated switch.
    /// For each local sibling, finds the slot whose
    /// <see cref="HostBinding.ReceiverName"/> matches the target hostname and
    /// writes <c>CHANGE_HOST(matching_slot)</c> — which may be a different
    /// slot index per device.
    /// </summary>
    /// <param name="targetHostName">Destination host name as recorded in each
    /// device's HostBindings — the system hostname of the target computer.
    /// Case-insensitive match.</param>
    /// <param name="originatingDeviceWpid">Optional — when set, skip the
    /// device with this WPID (it already left). Pass null for a fan-out with
    /// no originator (e.g. CLI-initiated switch).</param>
    /// <param name="source">Tag so subscribers know which path fired.</param>
    /// <returns>Number of siblings fanned out.</returns>
    public int RequestTopologyFanOut(string targetHostName, ushort? originatingDeviceWpid, FanOutSource source = FanOutSource.RemoteTopology)
    {
        var count = 0;
        var receiverCount = _manager.Receivers.Items.Count();
        _logger.LogInformation(
            "FanOut starting: targetHostName='{Target}', originatorWpid={Wpid}, source={Source}, receivers={N}",
            targetHostName, originatingDeviceWpid?.ToString("X4") ?? "(none)", source, receiverCount);

        // Emit a trigger for locally-initiated switches (e.g. CLI / user request)
        // so the topology layer broadcasts intent. RemoteTopology MUST NOT emit
        // here — that would create a peer-to-peer rebroadcast loop.
        if (source != FanOutSource.RemoteTopology)
        {
            _triggers.OnNext(new LocalSwitchTrigger(
                OriginatingReceiver: null,
                OriginatingSlot: 0,
                OriginatingDeviceSerial: null,
                OriginatingDeviceWpid: originatingDeviceWpid,
                TargetHostName: targetHostName,
                Source: source));
        }
        foreach (var receiver in _manager.Receivers.Items)
        {
            var deviceCount = receiver.Devices.Items.Count();
            _logger.LogInformation("FanOut: scanning receiver {Serial} ({N} devices)", receiver.Info.Serial, deviceCount);
            foreach (var device in receiver.Devices.Items)
            {
                if (originatingDeviceWpid is { } wpid && device.Wpid == wpid)
                {
                    _logger.LogInformation("FanOut: slot {Slot} wpid {DevWpid:X4} == originator — skipping", device.DeviceIndex, device.Wpid);
                    continue;
                }
                if (!device.CanReceiveHostSwitch)
                {
                    _logger.LogInformation("FanOut: slot {Slot} ({Name}) wpid {Wpid:X4} CanReceiveHostSwitch=false (ChangeHostIndex null) — skipping",
                        device.DeviceIndex, device.DisplayName, device.Wpid);
                    continue;
                }
                if (!device.LinkUp)
                {
                    _logger.LogInformation("FanOut: slot {Slot} ({Name}) wpid {Wpid:X4} LinkUp=false — skipping",
                        device.DeviceIndex, device.DisplayName, device.Wpid);
                    continue;
                }

                var matchingSlot = device.FindHostSlotByReceiverName(targetHostName);
                if (matchingSlot is not byte slot)
                {
                    var bindingsDump = string.Join(", ", device.HostBindings.Select(kv =>
                        $"h{kv.Key}={(kv.Value.Paired ? kv.Value.ReceiverName ?? "(no name)" : "unpaired")}"));
                    _logger.LogWarning(
                        "FanOut: slot {Slot} ({Name}) wpid {Wpid:X4} no binding to host '{Target}' — bindings: [{Dump}] — skipping",
                        device.DeviceIndex, device.DisplayName, device.Wpid, targetHostName, bindingsDump);
                    continue;
                }

                if (receiver.TrySwitchHost(device.DeviceIndex, slot))
                {
                    _fanOuts.OnNext(new FanOutEvent(
                        Target: device,
                        TargetHost: slot,
                        Source: source,
                        OriginatingReceiver: receiver,
                        OriginatingSlot: 0));
                    count++;

                    // Verify-and-retry. CHANGE_HOST is fire-and-forget — the
                    // firmware silently ignores writes to slots not paired
                    // to a reachable host, and we've observed cases where the
                    // first write doesn't take. If the device is still LinkUp
                    // ~1s later, it never left — try again up to 2 more times.
                    var rcvr = receiver; var dev = device; var tgt = slot;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            for (var attempt = 1; attempt <= 2; attempt++)
                            {
                                await Task.Delay(1200).ConfigureAwait(false);
                                if (!dev.LinkUp) return; // success — device disconnected
                                _logger.LogWarning(
                                    "CHANGE_HOST appears to have been ignored — device {Slot} ({Name}) still online {Ms}ms after write; retry #{N}",
                                    dev.DeviceIndex, dev.DisplayName, 1200 * attempt, attempt);
                                try { rcvr.TrySwitchHost(dev.DeviceIndex, tgt); } catch { }
                            }
                            if (dev.LinkUp)
                            {
                                _logger.LogWarning(
                                    "CHANGE_HOST gave up — device {Slot} ({Name}) still online after 3 attempts. Likely firmware-rejected (slot {Tgt} unpaired or unreachable).",
                                    dev.DeviceIndex, dev.DisplayName, tgt);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Outer guard — without this, any unobserved
                            // exception in the retry loop disappears into
                            // TaskScheduler.UnobservedTaskException and we
                            // lose the diagnostic.
                            _logger.LogError(ex,
                                "Verify-and-retry crashed for slot {Slot} ({Name})",
                                dev.DeviceIndex, dev.DisplayName);
                        }
                    });
                }
            }
        }
        _logger.LogInformation(
            "Topology fan-out (source={Source}): target host '{Target}', originator wpid={Wpid}, {Count} device(s) switched",
            source, targetHostName,
            originatingDeviceWpid?.ToString("X4") ?? "(none)",
            count);
        return count;
    }

    private void OnHostSwitchPressed(BoltReceiver origin, DivertedButtonsNotification press)
    {
        if (press.TargetHost is not int target) return;
        var targetHostIndex = (byte)target;
        FanOut(origin, press.DeviceIndex, targetHostIndex, FanOutSource.EasySwitchPress);
    }

    private void OnFlowHostSwitchDetected(BoltReceiver origin, ChangeHostWriteSnoop snoop)
    {
        FanOut(origin, snoop.DeviceIndex, snoop.TargetHost, FanOutSource.FlowSnoop);
    }

    private void FanOut(BoltReceiver origin, byte originatingSlot, byte originHostIndex, FanOutSource source)
    {
        var originDevice = origin.TryGetDevice(originatingSlot);
        var targetHostName = originDevice is not null
                             && originDevice.HostBindings.TryGetValue(originHostIndex, out var binding)
                             && binding.Paired
            ? binding.ReceiverName
            : null;

        if (string.IsNullOrWhiteSpace(targetHostName))
        {
            _logger.LogWarning(
                "Fan-out trigger from {Serial} slot {Slot} host {Host} (source={Source}) — origin device has no host name for that slot; cannot fan out. " +
                "Re-pair the device's host slot from each peer machine to populate the host name.",
                origin.Info.Serial, originatingSlot, originHostIndex, source);
            return;
        }

        _logger.LogInformation(
            "Fan-out trigger from {Serial} slot {Slot} -> host '{Target}' (source={Source})",
            origin.Info.Serial, originatingSlot, targetHostName, source);

        _triggers.OnNext(new LocalSwitchTrigger(
            OriginatingReceiver: origin,
            OriginatingSlot: originatingSlot,
            OriginatingDeviceSerial: originDevice?.Serial,
            OriginatingDeviceWpid: originDevice?.Wpid,
            TargetHostName: targetHostName,
            Source: source));

        foreach (var receiver in _manager.Receivers.Items)
        {
            foreach (var device in receiver.Devices.Items)
            {
                if (ReferenceEquals(receiver, origin) && device.DeviceIndex == originatingSlot)
                    continue;
                if (!device.CanReceiveHostSwitch) continue;
                if (!device.LinkUp) continue;

                var matchingSlot = device.FindHostSlotByReceiverName(targetHostName);
                if (matchingSlot is not byte slot)
                {
                    _logger.LogDebug(
                        "Sibling {Serial} slot {Slot} ({Name}) has no binding to host '{Target}' — skipping",
                        receiver.Info.Serial, device.DeviceIndex, device.DisplayName, targetHostName);
                    continue;
                }

                if (receiver.TrySwitchHost(device.DeviceIndex, slot))
                {
                    _fanOuts.OnNext(new FanOutEvent(
                        Target: device,
                        TargetHost: slot,
                        Source: source,
                        OriginatingReceiver: origin,
                        OriginatingSlot: originatingSlot));
                }
            }
        }
    }
}

/// <summary>Diagnostic event for the CLI / UI.</summary>
public sealed record FanOutEvent(
    PairedDevice Target,
    byte TargetHost,
    FanOutSource Source,
    BoltReceiver OriginatingReceiver,
    byte OriginatingSlot);

/// <summary>
/// One emission per Easy-Switch press / Flow snoop / user request, fired
/// after the target hostname has been resolved from the originator's
/// HostBindings and BEFORE the local sibling loop runs. Carries the data
/// the topology layer needs to broadcast intent to peer machines.
/// </summary>
public sealed record LocalSwitchTrigger(
    BoltReceiver? OriginatingReceiver,
    byte OriginatingSlot,
    string? OriginatingDeviceSerial,
    ushort? OriginatingDeviceWpid,
    string TargetHostName,
    FanOutSource Source);

/// <summary>What triggered the fan-out.</summary>
public enum FanOutSource
{
    EasySwitchPress,
    FlowSnoop,
    /// <summary>User-initiated switch with no originating device (CLI / API caller).</summary>
    UserRequested,
    RemoteTopology,
}
