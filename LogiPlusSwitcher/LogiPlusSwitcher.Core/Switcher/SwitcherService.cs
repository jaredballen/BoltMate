using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;
using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Core.HidPp.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogiPlusSwitcher.Core.Switcher;

/// <summary>
/// Multi-receiver, topology-aware fan-out orchestrator. One instance per
/// <see cref="ReceiverManager"/> (NOT per receiver). Subscribes to every
/// attached <see cref="BoltReceiver"/>'s host-switch streams and routes the
/// trigger across ALL participating receivers using each device's
/// <see cref="PairedDevice.HostBindings"/> for BLE-address matching.
/// </summary>
/// <remarks>
/// Algorithm (per trigger event):
/// <list type="number">
/// <item>Resolve the originating device's target BLE from its HostBindings.</item>
/// <item>For each sibling on any participating receiver, find the slot whose
/// binding points to the same BLE; CHANGE_HOST to that slot.</item>
/// <item>Skip the originator. Skip non-participating receivers. Skip siblings
/// without a matching binding (logged for UI hint).</item>
/// </list>
/// Falls back to legacy "same host index for every sibling" when the
/// originator's HostBindings aren't populated yet (e.g. first press before
/// DeviceEnricher has finished).
/// </remarks>
public sealed class SwitcherService : IDisposable
{
    private readonly ReceiverManager _manager;
    private readonly ILogger<SwitcherService> _logger;
    private readonly Subject<FanOutEvent> _fanOuts = new();
    private readonly CompositeDisposable _disposables = new();

    /// <summary>Stream of fan-out writes issued. One per sibling per trigger.</summary>
    public IObservable<FanOutEvent> FanOuts => _fanOuts.AsObservable();

    public SwitcherService(ReceiverManager manager, ILogger<SwitcherService>? logger = null)
    {
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
    }

    public void Dispose() => _disposables.Dispose();

    /// <summary>
    /// User-initiated fan-out — write <c>CHANGE_HOST(targetHost)</c> to every
    /// online, host-switch-capable device on every participating receiver.
    /// Used by global-hotkey handlers; no topology lookup involved (user is
    /// being explicit about the target slot).
    /// </summary>
    /// <returns>Number of devices the switch was issued to.</returns>
    public int RequestUserFanOut(byte targetHost)
    {
        var count = 0;
        foreach (var receiver in _manager.Receivers.Items)
        {
            if (!receiver.IsParticipating) continue;
            foreach (var device in receiver.Devices.Items)
            {
                if (!device.CanReceiveHostSwitch) continue;
                if (!device.LinkUp) continue;
                if (receiver.TrySwitchHost(device.DeviceIndex, targetHost))
                {
                    _fanOuts.OnNext(new FanOutEvent(
                        Target: device,
                        TargetHost: targetHost,
                        Source: FanOutSource.UserHotkey,
                        OriginatingReceiver: receiver,
                        OriginatingSlot: 0));
                    count++;
                }
            }
        }
        _logger.LogInformation("User hotkey fan-out: target host {Host}, {Count} device(s) switched", targetHost, count);
        return count;
    }

    /// <summary>
    /// Topology correlator fan-out — a remote machine reported that one of
    /// our devices just came online there. Match by the remote receiver's BLE
    /// address against each local sibling's <see cref="PairedDevice.HostBindings"/>
    /// and route the matching slot. Devices without a binding pointing at the
    /// remote BLE are skipped (logged).
    /// </summary>
    /// <param name="remoteReceiverBleKey">Lowercase hex BLE address of the
    /// remote receiver where the device re-appeared.</param>
    /// <param name="originatingDeviceWpid">The device that left us — used to
    /// avoid trying to switch it back (it already left).</param>
    /// <returns>Number of siblings fanned out.</returns>
    public int RequestTopologyFanOut(string remoteReceiverBleKey, ushort? originatingDeviceWpid)
    {
        var count = 0;
        foreach (var receiver in _manager.Receivers.Items)
        {
            if (!receiver.IsParticipating) continue;
            foreach (var device in receiver.Devices.Items)
            {
                if (originatingDeviceWpid is { } wpid && device.Wpid == wpid) continue;
                if (!device.CanReceiveHostSwitch) continue;
                if (!device.LinkUp) continue;

                var matchingSlot = device.FindHostSlotForBleKey(remoteReceiverBleKey);
                if (matchingSlot is not byte slot)
                {
                    _logger.LogDebug(
                        "Sibling {Serial} slot {Slot} ({Name}) has no binding to remote BLE {Ble} — skipping",
                        receiver.Info.Serial, device.DeviceIndex, device.DisplayName, remoteReceiverBleKey);
                    continue;
                }

                if (receiver.TrySwitchHost(device.DeviceIndex, slot))
                {
                    _fanOuts.OnNext(new FanOutEvent(
                        Target: device,
                        TargetHost: slot,
                        Source: FanOutSource.RemoteTopology,
                        OriginatingReceiver: receiver,
                        OriginatingSlot: 0));
                    count++;
                }
            }
        }
        _logger.LogInformation(
            "Topology fan-out: remote BLE {Ble}, originator wpid={Wpid}, {Count} device(s) switched",
            remoteReceiverBleKey,
            originatingDeviceWpid?.ToString("X4") ?? "?",
            count);
        return count;
    }

    private void OnHostSwitchPressed(BoltReceiver origin, DivertedButtonsNotification press)
    {
        if (!origin.IsParticipating)
        {
            _logger.LogInformation("Ignoring Easy-Switch press on non-participating receiver {Serial}", origin.Info.Serial);
            return;
        }
        if (press.TargetHost is not int target) return;
        var targetHostIndex = (byte)target;
        FanOut(origin, press.DeviceIndex, targetHostIndex, FanOutSource.EasySwitchPress);
    }

    private void OnFlowHostSwitchDetected(BoltReceiver origin, ChangeHostWriteSnoop snoop)
    {
        if (!origin.IsParticipating) return;
        FanOut(origin, snoop.DeviceIndex, snoop.TargetHost, FanOutSource.FlowSnoop);
    }

    private void FanOut(BoltReceiver origin, byte originatingSlot, byte originHostIndex, FanOutSource source)
    {
        var originDevice = origin.TryGetDevice(originatingSlot);
        var targetBleKey = originDevice is not null
                           && originDevice.HostBindings.TryGetValue(originHostIndex, out var binding)
                           && binding.Paired
            ? binding.BluetoothAddressKey
            : null;

        if (targetBleKey is null)
        {
            _logger.LogInformation(
                "Fan-out trigger from {Serial} slot {Slot} host {Host} (source={Source}) — no BLE binding cached; falling back to same-index routing",
                origin.Info.Serial, originatingSlot, originHostIndex, source);
            FanOutLegacy(origin, originatingSlot, originHostIndex, source);
            return;
        }

        _logger.LogInformation(
            "Fan-out trigger from {Serial} slot {Slot} -> BLE {Ble} (source={Source})",
            origin.Info.Serial, originatingSlot, targetBleKey, source);

        foreach (var receiver in _manager.Receivers.Items)
        {
            if (!receiver.IsParticipating) continue;
            foreach (var device in receiver.Devices.Items)
            {
                if (ReferenceEquals(receiver, origin) && device.DeviceIndex == originatingSlot)
                    continue;
                if (!device.CanReceiveHostSwitch) continue;
                if (!device.LinkUp) continue;

                var matchingSlot = device.FindHostSlotForBleKey(targetBleKey);
                if (matchingSlot is not byte slot)
                {
                    _logger.LogDebug(
                        "Sibling {Serial} slot {Slot} ({Name}) has no binding to BLE {Ble} — skipping",
                        receiver.Info.Serial, device.DeviceIndex, device.DisplayName, targetBleKey);
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

    /// <summary>
    /// Falls back to the pre-topology behavior: send the same host index to
    /// every sibling. Used when the originator's HostBindings are not yet
    /// populated (e.g. the very first press, before background enrichment).
    /// </summary>
    private void FanOutLegacy(BoltReceiver origin, byte originatingSlot, byte targetHost, FanOutSource source)
    {
        foreach (var receiver in _manager.Receivers.Items)
        {
            if (!receiver.IsParticipating) continue;
            foreach (var device in receiver.Devices.Items)
            {
                if (ReferenceEquals(receiver, origin) && device.DeviceIndex == originatingSlot)
                    continue;
                if (!device.CanReceiveHostSwitch) continue;
                if (!device.LinkUp) continue;

                if (receiver.TrySwitchHost(device.DeviceIndex, targetHost))
                {
                    _fanOuts.OnNext(new FanOutEvent(
                        Target: device,
                        TargetHost: targetHost,
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

/// <summary>What triggered the fan-out.</summary>
public enum FanOutSource
{
    EasySwitchPress,
    FlowSnoop,
    UserHotkey,
    RemoteTopology,
}
