using System.Collections.Concurrent;
using LogiPlusSwitcher.Core.Hid;
using LogiPlusSwitcher.Core.HidPp;
using LogiPlusSwitcher.Core.HidPp.Features;
using LogiPlusSwitcher.Core.HidPp.Notifications;

namespace LogiPlusSwitcher.Core.Bolt;

/// <summary>
/// High-level controller for a single Bolt receiver. Owns the underlying HID
/// connection, the HID++ client, and the paired-device table. Routes inbound
/// notifications to the right parsers and exposes app-level events.
/// </summary>
public sealed class BoltReceiver : IDisposable
{
    private readonly IReceiverConnection _connection;
    private readonly HidPpClient _client;
    private readonly ConcurrentDictionary<byte, PairedDevice> _devices = new();
    private bool _started;
    private bool _disposed;

    public BoltReceiverInfo Info { get; }

    public RootService Root { get; }
    public ChangeHostService ChangeHost { get; }
    public HostsInfoService HostsInfo { get; }
    public ReprogControlsService ReprogControls { get; }

    /// <summary>The HID++ client (escape hatch — most callers use the typed services above).</summary>
    public HidPpClient Client => _client;

    /// <summary>Snapshot of paired devices known on this receiver.</summary>
    public IReadOnlyCollection<PairedDevice> Devices => _devices.Values.ToList();

    /// <summary>Fires when a 0x41 DJ_PAIRING notification reports a slot transitioning up.</summary>
    public event EventHandler<PairedDevice>? DeviceLinkEstablished;

    /// <summary>Fires when a 0x41 DJ_PAIRING notification reports a slot losing its link.</summary>
    public event EventHandler<PairedDevice>? DeviceLinkLost;

    /// <summary>
    /// Fires when a diverted Easy-Switch CID press is observed. Payload's
    /// <see cref="DivertedButtonsNotification.TargetHost"/> is the host the
    /// device is about to switch to.
    /// </summary>
    public event EventHandler<DivertedButtonsNotification>? HostSwitchPressed;

    /// <summary>
    /// Fires when a HID++ <c>SetCurrentHost</c> write from another piece of
    /// software (Logi Options+ doing a Flow handover) is seen on the wire.
    /// </summary>
    public event EventHandler<ChangeHostWriteSnoop>? FlowHostSwitchDetected;

    /// <summary>Generic dump of every inbound frame — for diagnostics / CLI.</summary>
    public event EventHandler<HidPpFrame>? RawFrameReceived;

    public BoltReceiver(BoltReceiverInfo info, IReceiverConnection connection, HidPpClient? client = null)
    {
        Info = info;
        _connection = connection;
        _client = client ?? new HidPpClient(connection);

        Root = new RootService(_client);
        ChangeHost = new ChangeHostService(_client);
        HostsInfo = new HostsInfoService(_client);
        ReprogControls = new ReprogControlsService(_client);

        _client.NotificationReceived += OnNotificationReceived;
    }

    /// <summary>
    /// Starts the underlying read pump and asks the receiver to enable HID++
    /// notifications + re-enumerate paired devices. Idempotent.
    /// </summary>
    public void Start()
    {
        if (_started)
            return;
        _started = true;

        _connection.Start();
        _client.SendOneWay(HidPp10.BuildEnableNotificationsFrame());
        _client.SendOneWay(HidPp10.BuildEnumerateDevicesFrame());
    }

    /// <summary>
    /// Resolves the feature indices we care about on a given slot. Safe to
    /// call multiple times — null feature indices are simply re-queried.
    /// </summary>
    public async Task DiscoverFeaturesAsync(byte deviceIndex, CancellationToken ct = default)
    {
        var device = _devices.GetOrAdd(deviceIndex, _ => new PairedDevice(deviceIndex));

        device.ReprogControlsIndex ??= (await Root.GetFeatureAsync(deviceIndex, FeatureIds.ReprogControlsV4, ct).ConfigureAwait(false))?.Index;
        device.ChangeHostIndex ??= (await Root.GetFeatureAsync(deviceIndex, FeatureIds.ChangeHost, ct).ConfigureAwait(false))?.Index;
        device.HostsInfoIndex ??= (await Root.GetFeatureAsync(deviceIndex, FeatureIds.HostsInfo, ct).ConfigureAwait(false))?.Index;
    }

    /// <summary>
    /// Enumerates the device's reprogrammable controls, finds the Easy-Switch
    /// CIDs that are divertable, and diverts them so subsequent presses fire
    /// <see cref="HostSwitchPressed"/> instead of executing internally.
    /// </summary>
    public async Task<IReadOnlyList<ushort>> DivertHostSwitchCidsAsync(byte deviceIndex, bool persistent = false, CancellationToken ct = default)
    {
        var device = _devices.GetOrAdd(deviceIndex, _ => new PairedDevice(deviceIndex));
        if (device.ReprogControlsIndex is not { } reprogIndex)
            return [];

        var controls = await ReprogControls.ListControlsAsync(deviceIndex, reprogIndex, ct).ConfigureAwait(false);

        var diverted = new List<ushort>();
        var bfield = persistent
            ? ReprogControlsService.DivertModes.DivertedPersistent
            : ReprogControlsService.DivertModes.Diverted;

        foreach (var control in controls)
        {
            if (!control.IsHostSwitch)
                continue;
            if (!control.IsDivertable && !control.IsPersistentlyDivertable)
                continue;
            if (persistent && !control.IsPersistentlyDivertable)
                continue;

            await ReprogControls.SetCidReportingAsync(deviceIndex, reprogIndex, control.ControlId, bfield, ct).ConfigureAwait(false);
            diverted.Add(control.ControlId);
        }

        device.DivertedHostSwitchCids = diverted;
        return diverted;
    }

    /// <summary>
    /// Restores default behaviour for any Easy-Switch CIDs that were diverted
    /// on this slot. Call on shutdown so the device still works when our app
    /// isn't running.
    /// </summary>
    public async Task RestoreHostSwitchCidsAsync(byte deviceIndex, CancellationToken ct = default)
    {
        if (!_devices.TryGetValue(deviceIndex, out var device))
            return;
        if (device.ReprogControlsIndex is not { } reprogIndex)
            return;
        if (device.DivertedHostSwitchCids.Count == 0)
            return;

        foreach (var cid in device.DivertedHostSwitchCids)
        {
            try
            {
                await ReprogControls.SetCidReportingAsync(
                    deviceIndex, reprogIndex, cid, ReprogControlsService.DivertModes.Normal, ct).ConfigureAwait(false);
            }
            catch (HidPpException)
            {
                // Best effort — device may be offline.
            }
        }
        device.DivertedHostSwitchCids = [];
    }

    /// <summary>
    /// Issues a fire-and-forget host switch to a single paired device.
    /// </summary>
    public bool TrySwitchHost(byte deviceIndex, byte targetHost)
    {
        if (!_devices.TryGetValue(deviceIndex, out var device))
            return false;
        if (device.ChangeHostIndex is not { } featureIndex)
            return false;

        ChangeHost.SetCurrentHost(deviceIndex, featureIndex, targetHost);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _client.NotificationReceived -= OnNotificationReceived;
        _client.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Pumps an inbound notification through every parser we know and raises
    /// the matching app-level event. A frame can be both a Bolt link-state
    /// change AND match some other parser; we route by best fit.
    /// </summary>
    private void OnNotificationReceived(object? sender, HidPpFrame frame)
    {
        RawFrameReceived?.Invoke(this, frame);

        if (DjPairingNotification.TryParse(frame, out var pairing))
        {
            var device = _devices.GetOrAdd(pairing.DeviceIndex, _ => new PairedDevice(pairing.DeviceIndex));
            device.Wpid = pairing.Wpid;
            device.LinkUp = pairing.LinkEstablished;

            if (pairing.LinkEstablished)
                DeviceLinkEstablished?.Invoke(this, device);
            else
                DeviceLinkLost?.Invoke(this, device);
            return;
        }

        // Per-slot feature-index routing for HID++ 2.0 notifications.
        if (_devices.TryGetValue(frame.DeviceIndex, out var slot))
        {
            if (slot.ReprogControlsIndex is { } reprogIndex
                && DivertedButtonsNotification.TryParse(frame, reprogIndex, out var divertedPress))
            {
                HostSwitchPressed?.Invoke(this, divertedPress);
                return;
            }

            if (slot.ChangeHostIndex is { } changeHostIndex
                && ChangeHostWriteSnoop.TryParse(frame, changeHostIndex, _client.SwId, out var snoop))
            {
                FlowHostSwitchDetected?.Invoke(this, snoop);
                return;
            }
        }
    }
}
