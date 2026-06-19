using System.Reactive.Disposables;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Core.HidPp.Notifications;

namespace LogiPlusSwitcher.Core.Switcher;

/// <summary>
/// Orchestrator — listens for any host-switch trigger on a receiver (either
/// a diverted Easy-Switch press OR a Logi Options+ Flow write) and fans the
/// switch out to every OTHER paired device that supports CHANGE_HOST.
/// </summary>
/// <remarks>
/// Two trigger paths:
/// <list type="number">
/// <item><description><see cref="BoltReceiver.HostSwitchPresses"/> — diverted CID,
/// target host arrives in payload BEFORE the originating device disconnects.</description></item>
/// <item><description><see cref="BoltReceiver.FlowHostSwitches"/> — Logi+ wrote
/// CHANGE_HOST to a mouse during a Flow handover; we echo it to siblings.</description></item>
/// </list>
/// In both cases the originating slot is excluded from the fan-out.
/// </remarks>
public sealed class SwitcherService : IDisposable
{
    private readonly BoltReceiver _receiver;
    private readonly Subject<FanOutEvent> _fanOuts = new();
    private readonly CompositeDisposable _disposables = new();

    /// <summary>Stream of fan-out writes issued. One per sibling device per trigger.</summary>
    public IObservable<FanOutEvent> FanOuts => _fanOuts.AsObservable();

    public SwitcherService(BoltReceiver receiver)
    {
        _receiver = receiver;

        _disposables.Add(_receiver.HostSwitchPresses.Subscribe(OnHostSwitchPressed));
        _disposables.Add(_receiver.FlowHostSwitches.Subscribe(OnFlowHostSwitchDetected));
        _disposables.Add(_fanOuts);
    }

    public void Dispose() => _disposables.Dispose();

    private void OnHostSwitchPressed(DivertedButtonsNotification press)
    {
        if (press.TargetHost is not { } host)
            return;
        FanOut(originatingSlot: press.DeviceIndex, targetHost: (byte)host, source: FanOutSource.EasySwitchPress);
    }

    private void OnFlowHostSwitchDetected(ChangeHostWriteSnoop snoop)
    {
        FanOut(originatingSlot: snoop.DeviceIndex, targetHost: snoop.TargetHost, source: FanOutSource.FlowSnoop);
    }

    private void FanOut(byte originatingSlot, byte targetHost, FanOutSource source)
    {
        foreach (var device in _receiver.Devices.Items)
        {
            if (device.DeviceIndex == originatingSlot)
                continue;
            if (!device.CanReceiveHostSwitch)
                continue;
            if (!device.LinkUp)
                continue;

            if (_receiver.TrySwitchHost(device.DeviceIndex, targetHost))
                _fanOuts.OnNext(new FanOutEvent(device, targetHost, source, originatingSlot));
        }
    }
}

/// <summary>Diagnostic event for the CLI / UI.</summary>
public sealed record FanOutEvent(PairedDevice Target, byte TargetHost, FanOutSource Source, byte OriginatingSlot);

/// <summary>What triggered the fan-out.</summary>
public enum FanOutSource
{
    /// <summary>A diverted Easy-Switch CID was pressed on a paired device.</summary>
    EasySwitchPress,
    /// <summary>Logi Options+ wrote SetCurrentHost during a Flow handover.</summary>
    FlowSnoop,
    /// <summary>The user triggered a host switch via our app's CLI or UI.</summary>
    UserHotkey,
}
