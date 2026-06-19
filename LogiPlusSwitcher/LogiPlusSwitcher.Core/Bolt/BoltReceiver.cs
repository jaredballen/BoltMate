using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;
using LogiPlusSwitcher.Core.Hid;
using LogiPlusSwitcher.Core.HidPp;
using LogiPlusSwitcher.Core.HidPp.Features;
using LogiPlusSwitcher.Core.HidPp.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogiPlusSwitcher.Core.Bolt;

/// <summary>
/// High-level controller for a single Bolt receiver. Owns the underlying HID
/// connection, the HID++ client, and the paired-device cache. Routes inbound
/// notifications to the right parsers and exposes app-level streams.
/// </summary>
public sealed class BoltReceiver : IDisposable
{
    private readonly IReceiverConnection _connection;
    private readonly HidPpClient _client;
    private readonly ILogger<BoltReceiver> _logger;
    private readonly SourceCache<PairedDevice, byte> _devicesCache;
    private readonly Subject<DivertedButtonsNotification> _hostSwitchPressed = new();
    private readonly Subject<ChangeHostWriteSnoop> _flowHostSwitchDetected = new();
    private readonly Subject<HidPpFrame> _rawFrames = new();
    private readonly Subject<PairedDevice> _linkEstablished = new();
    private readonly Subject<PairedDevice> _linkLost = new();
    private readonly CompositeDisposable _disposables = new();
    private bool _started;

    public BoltReceiverInfo Info { get; }

    public RootService Root { get; }
    public ChangeHostService ChangeHost { get; }
    public HostsInfoService HostsInfo { get; }
    public ReprogControlsService ReprogControls { get; }

    /// <summary>The HID++ client (escape hatch — most callers use the typed services above).</summary>
    public HidPpClient Client => _client;

    /// <summary>Live cache of paired devices on this receiver, keyed by slot index 1..6.</summary>
    public IObservableCache<PairedDevice, byte> Devices { get; }

    /// <summary>
    /// Stream of diverted Easy-Switch CID presses. Payload's
    /// <see cref="DivertedButtonsNotification.TargetHost"/> is the host the
    /// device is about to switch to.
    /// </summary>
    public IObservable<DivertedButtonsNotification> HostSwitchPresses =>
        _hostSwitchPressed.AsObservable();

    /// <summary>
    /// Stream of foreign <c>SetCurrentHost</c> writes — Logi Options+ doing a
    /// Mouse Flow handover that we want to fan out to siblings.
    /// </summary>
    public IObservable<ChangeHostWriteSnoop> FlowHostSwitches =>
        _flowHostSwitchDetected.AsObservable();

    /// <summary>Diagnostic dump of every inbound frame (CLI / debug).</summary>
    public IObservable<HidPpFrame> RawFrames => _rawFrames.AsObservable();

    /// <summary>Stream of slots that just gained a wireless link.</summary>
    public IObservable<PairedDevice> LinkEstablished => _linkEstablished.AsObservable();

    /// <summary>Stream of slots that just lost their wireless link.</summary>
    public IObservable<PairedDevice> LinkLost => _linkLost.AsObservable();

    public BoltReceiver(BoltReceiverInfo info, IReceiverConnection connection, HidPpClient? client = null, ILogger<BoltReceiver>? logger = null)
    {
        Info = info;
        _connection = connection;
        _client = client ?? new HidPpClient(connection);
        _logger = logger ?? NullLogger<BoltReceiver>.Instance;
        _devicesCache = new SourceCache<PairedDevice, byte>(d => d.DeviceIndex);

        Devices = _devicesCache.AsObservableCache();

        Root = new RootService(_client);
        ChangeHost = new ChangeHostService(_client);
        HostsInfo = new HostsInfoService(_client);
        ReprogControls = new ReprogControlsService(_client);

        _disposables.Add(_client.Notifications.Subscribe(OnNotification));
        _disposables.Add(_devicesCache);
        _disposables.Add((IDisposable)Devices);
        _disposables.Add(_hostSwitchPressed);
        _disposables.Add(_flowHostSwitchDetected);
        _disposables.Add(_rawFrames);
        _disposables.Add(_linkEstablished);
        _disposables.Add(_linkLost);
        _disposables.Add(_client);
        _disposables.Add(_connection);
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

        _logger.LogInformation("Starting receiver {Serial} ({Product})", Info.Serial, Info.ProductString);
        _connection.Start();
        _client.SendOneWay(HidPp10.BuildEnableNotificationsFrame());
        _client.SendOneWay(HidPp10.BuildEnumerateDevicesFrame());
    }

    /// <summary>
    /// Returns the cached <see cref="PairedDevice"/> for a slot, or null.
    /// Mutations to the returned instance should be followed by
    /// <see cref="RefreshSlot"/> so subscribers see the change.
    /// </summary>
    public PairedDevice? TryGetDevice(byte deviceIndex) =>
        _devicesCache.Lookup(deviceIndex).HasValue
            ? _devicesCache.Lookup(deviceIndex).Value
            : null;

    /// <summary>
    /// Returns the cached <see cref="PairedDevice"/> for a slot, creating an
    /// empty one if it does not yet exist (e.g. tests seeding state, or the
    /// post-link-up discovery flow caching feature indices). Subscribers of
    /// <see cref="Devices"/> see an Add when this creates a new entry.
    /// </summary>
    public PairedDevice EnsureSlot(byte deviceIndex)
    {
        var existing = _devicesCache.Lookup(deviceIndex);
        if (existing.HasValue)
            return existing.Value;

        var device = new PairedDevice(deviceIndex);
        _devicesCache.AddOrUpdate(device);
        return device;
    }

    /// <summary>
    /// Notifies <see cref="Devices"/> subscribers that a slot's properties
    /// have changed (DynamicData refresh — same identity, mutated fields).
    /// </summary>
    public void RefreshSlot(byte deviceIndex)
    {
        var entry = _devicesCache.Lookup(deviceIndex);
        if (entry.HasValue)
            _devicesCache.Refresh(entry.Value);
    }

    /// <summary>
    /// Resolves the feature indices we care about on a given slot. Safe to
    /// call multiple times — null feature indices are simply re-queried.
    /// </summary>
    public async Task DiscoverFeaturesAsync(byte deviceIndex, CancellationToken ct = default)
    {
        var device = EnsureSlot(deviceIndex);

        device.ReprogControlsIndex ??= (await Root.GetFeatureAsync(deviceIndex, FeatureIds.ReprogControlsV4, ct).ConfigureAwait(false))?.Index;
        device.ChangeHostIndex ??= (await Root.GetFeatureAsync(deviceIndex, FeatureIds.ChangeHost, ct).ConfigureAwait(false))?.Index;
        device.HostsInfoIndex ??= (await Root.GetFeatureAsync(deviceIndex, FeatureIds.HostsInfo, ct).ConfigureAwait(false))?.Index;

        RefreshSlot(deviceIndex);
    }

    /// <summary>
    /// Enumerates the device's reprogrammable controls, finds the Easy-Switch
    /// CIDs that are divertable, and diverts them so subsequent presses fire
    /// on <see cref="HostSwitchPresses"/> instead of executing internally.
    /// </summary>
    public async Task<IReadOnlyList<ushort>> DivertHostSwitchCidsAsync(byte deviceIndex, bool persistent = false, CancellationToken ct = default)
    {
        var device = EnsureSlot(deviceIndex);
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
        RefreshSlot(deviceIndex);
        return diverted;
    }

    /// <summary>
    /// Restores default behaviour for any Easy-Switch CIDs that were diverted
    /// on this slot. Call on shutdown so the device still works when our app
    /// isn't running.
    /// </summary>
    public async Task RestoreHostSwitchCidsAsync(byte deviceIndex, CancellationToken ct = default)
    {
        var device = TryGetDevice(deviceIndex);
        if (device is null) return;
        if (device.ReprogControlsIndex is not { } reprogIndex) return;
        if (device.DivertedHostSwitchCids.Count == 0) return;

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
        RefreshSlot(deviceIndex);
    }

    /// <summary>
    /// Issues a fire-and-forget host switch to a single paired device.
    /// </summary>
    public bool TrySwitchHost(byte deviceIndex, byte targetHost)
    {
        var device = TryGetDevice(deviceIndex);
        if (device is null) return false;
        if (device.ChangeHostIndex is not { } featureIndex) return false;

        ChangeHost.SetCurrentHost(deviceIndex, featureIndex, targetHost);
        return true;
    }

    public void Dispose() => _disposables.Dispose();

    private void OnNotification(HidPpFrame frame)
    {
        _rawFrames.OnNext(frame);

        if (DjPairingNotification.TryParse(frame, out var pairing))
        {
            var device = EnsureSlot(pairing.DeviceIndex);
            var previousLinkUp = device.LinkUp;
            device.Wpid = pairing.Wpid;
            device.LinkUp = pairing.LinkEstablished;
            RefreshSlot(pairing.DeviceIndex);

            if (pairing.LinkEstablished && !previousLinkUp)
            {
                _logger.LogInformation("Slot {Slot} link UP wpid=0x{Wpid:X4}", device.DeviceIndex, device.Wpid);
                _linkEstablished.OnNext(device);
            }
            else if (!pairing.LinkEstablished && previousLinkUp)
            {
                _logger.LogInformation("Slot {Slot} link LOST", device.DeviceIndex);
                _linkLost.OnNext(device);
            }
            else if (pairing.LinkEstablished)
            {
                // First-seen notification with LinkEstablished: emit as a link-up.
                _logger.LogInformation("Slot {Slot} link UP (first seen) wpid=0x{Wpid:X4}", device.DeviceIndex, device.Wpid);
                _linkEstablished.OnNext(device);
            }
            return;
        }

        // Per-slot feature-index routing for HID++ 2.0 notifications.
        var existing = _devicesCache.Lookup(frame.DeviceIndex);
        if (!existing.HasValue) return;
        var slot = existing.Value;

        if (slot.ReprogControlsIndex is { } reprogIndex
            && DivertedButtonsNotification.TryParse(frame, reprogIndex, out var divertedPress))
        {
            _hostSwitchPressed.OnNext(divertedPress);
            return;
        }

        if (slot.ChangeHostIndex is { } changeHostIndex
            && ChangeHostWriteSnoop.TryParse(frame, changeHostIndex, _client.SwId, out var snoop))
        {
            _flowHostSwitchDetected.OnNext(snoop);
        }
    }
}
