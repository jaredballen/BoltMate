using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using DynamicData;
using LogiPlusSwitcher.Hid.Abstractions;
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

    /// <summary>
    /// Whether this receiver participates in switch fan-out. Toggled by the
    /// App layer based on license / primary-receiver policy. Core never
    /// reads license state directly — it just respects this flag.
    /// </summary>
    /// <remarks>
    /// Defaults to true. Set to false to exclude this receiver from
    /// <see cref="Switcher.SwitcherService"/> routing while still allowing
    /// enumeration / metadata reads / explicit per-device CHANGE_HOST calls.
    /// </remarks>
    public bool IsParticipating
    {
        get => _isParticipating;
        set
        {
            if (_isParticipating == value) return;
            _isParticipating = value;
            _logger.LogInformation("Receiver {Serial} IsParticipating={Value}", Info.Serial, value);
            _participationChanged.OnNext(value);
        }
    }
    private bool _isParticipating = true;
    private readonly Subject<bool> _participationChanged = new();

    /// <summary>Hot stream of <see cref="IsParticipating"/> changes.</summary>
    public IObservable<bool> ParticipationChanges => _participationChanged.AsObservable();

    /// <summary>
    /// BLE address of this receiver as read from its own flash. Populated
    /// when <see cref="GetReceiverDetailsAsync"/> succeeds and exposes the
    /// address (best-effort). Null until then.
    /// </summary>
    public byte[]? HostIdentifier { get; set; }

    /// <summary>
    /// Most recent <see cref="ReceiverDetails"/> read from the receiver's
    /// flash via HID++ 1.0 registers (serial, firmware version, max devices,
    /// BLE address). Populated by <see cref="GetReceiverDetailsAsync"/> on
    /// every successful call and persisted here so the UI / diagnostics
    /// tree can render it without re-reading.
    /// </summary>
    public ReceiverDetails? LastKnownDetails { get; private set; }

    /// <summary>Stable lowercase hex string of <see cref="HostIdentifier"/>, or null.</summary>
    public string? HostIdentifierKey =>
        HostIdentifier is null ? null : Convert.ToHexString(HostIdentifier).ToLowerInvariant();

    public RootService Root { get; }
    public ChangeHostService ChangeHost { get; }
    public HostsInfoService HostsInfo { get; }
    public ReprogControlsService ReprogControls { get; }
    public DeviceNameService DeviceName { get; }
    public DeviceInfoService DeviceInfo { get; }
    public BatteryService Battery { get; }
    public DeviceFriendlyNameService DeviceFriendlyName { get; }

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
        DeviceName = new DeviceNameService(_client);
        DeviceInfo = new DeviceInfoService(_client);
        Battery = new BatteryService(_client);
        DeviceFriendlyName = new DeviceFriendlyNameService(_client);

        _disposables.Add(_client.Notifications.Subscribe(OnNotification));
        _disposables.Add(_devicesCache);
        _disposables.Add((IDisposable)Devices);
        _disposables.Add(_hostSwitchPressed);
        _disposables.Add(_flowHostSwitchDetected);
        _disposables.Add(_rawFrames);
        _disposables.Add(_linkEstablished);
        _disposables.Add(_linkLost);
        _disposables.Add(_participationChanged);
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
        device.DeviceInfoIndex ??= (await Root.GetFeatureAsync(deviceIndex, FeatureIds.DeviceInfo, ct).ConfigureAwait(false))?.Index;
        device.DeviceNameIndex ??= (await Root.GetFeatureAsync(deviceIndex, FeatureIds.DeviceName, ct).ConfigureAwait(false))?.Index;
        device.UnifiedBatteryIndex ??= (await Root.GetFeatureAsync(deviceIndex, FeatureIds.UnifiedBattery, ct).ConfigureAwait(false))?.Index;
        device.DeviceFriendlyNameIndex ??= (await Root.GetFeatureAsync(deviceIndex, FeatureIds.DeviceFriendlyName, ct).ConfigureAwait(false))?.Index;

        _logger.LogInformation(
            "Slot {Slot} feature indices: 1B04={Reprog} 1814={Ch} 1815={HI} 0003={DI} 0005={DN} 1004={Bat} 0007={FN}",
            deviceIndex,
            device.ReprogControlsIndex?.ToString("X2") ?? "-",
            device.ChangeHostIndex?.ToString("X2") ?? "-",
            device.HostsInfoIndex?.ToString("X2") ?? "-",
            device.DeviceInfoIndex?.ToString("X2") ?? "-",
            device.DeviceNameIndex?.ToString("X2") ?? "-",
            device.UnifiedBatteryIndex?.ToString("X2") ?? "-",
            device.DeviceFriendlyNameIndex?.ToString("X2") ?? "-");

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
    /// Reads an arbitrary short receiver register (HID++ 1.0 GET, sub-id 0x81).
    /// Returns the 3-byte register payload, or an empty array if the read failed.
    /// Used for diagnostics; e.g. reading register 0x00 to see the receiver's
    /// current notification-flags mask.
    /// </summary>
    public async Task<byte[]> ReadShortReceiverRegisterAsync(byte register, CancellationToken ct = default)
    {
        var reply = await Hidpp10ReadAsync(
            HidPp10.BuildReadShortRegisterFrame(register),
            expectedRegister: register,
            window: TimeSpan.FromMilliseconds(500),
            ct).ConfigureAwait(false);
        if (reply is null) return Array.Empty<byte>();
        return reply.Value.Parameters.ToArray();
    }

    /// <summary>
    /// Reads <c>BOLT_DEVICE_NAME</c> (sub-register <c>0x60 + slot</c>) for a single
    /// slot and stores it on the cached <see cref="PairedDevice"/>. Returns the
    /// name, or null if the read failed (slot empty / device offline).
    /// </summary>
    public async Task<string?> ReadSlotNameAsync(byte slot, CancellationToken ct = default)
    {
        if (slot is < HidPpConstants.DeviceIndexFirstSlot or > HidPpConstants.DeviceIndexLastSlot)
            return null;

        var subRegister = (byte)(HidPp10.InfoSubRegisterBoltDeviceNameBase + slot);
        var reply = await Hidpp10ReadAsync(
            HidPp10.BuildReadReceiverInfoFrame(subRegister, extraByte: 0x01),
            expectedRegister: HidPp10.RegisterReceiverInfo,
            window: TimeSpan.FromMilliseconds(500),
            ct).ConfigureAwait(false);

        if (reply is null) return null;

        // Reply payload layout (per Solaar BoltReceiver.device_codename):
        //   [0]=sub-register echo, [1]=0x01 echo, [2]=name length, [3..]=ASCII chars
        var p = reply.Value.Parameters.Span;
        if (p.Length < 4) return null;
        var nameLen = Math.Min((int)p[2], Math.Min(14, p.Length - 3));
        if (nameLen <= 0) return null;
        var name = System.Text.Encoding.ASCII.GetString(p.Slice(3, nameLen)).TrimEnd();

        var device = EnsureSlot(slot);
        device.Name = name;
        RefreshSlot(slot);
        return name;
    }

    /// <summary>
    /// Reads <c>BOLT_PAIRING_INFORMATION</c> (sub-register <c>0x50 + slot</c>) for
    /// a slot — wpid, serial, BLE address, protocol — and populates the cached
    /// <see cref="PairedDevice"/>.
    /// </summary>
    public async Task<bool> ReadSlotPairingInfoAsync(byte slot, CancellationToken ct = default)
    {
        if (slot is < HidPpConstants.DeviceIndexFirstSlot or > HidPpConstants.DeviceIndexLastSlot)
            return false;

        var subRegister = (byte)(HidPp10.InfoSubRegisterBoltPairingInfoBase + slot);
        var reply = await Hidpp10ReadAsync(
            HidPp10.BuildReadReceiverInfoFrame(subRegister),
            expectedRegister: HidPp10.RegisterReceiverInfo,
            window: TimeSpan.FromMilliseconds(500),
            ct).ConfigureAwait(false);

        if (reply is null) return false;

        // Solaar BoltReceiver.device_pairing_information parses:
        //   wpid = bytes 3..4 swapped LE  ([3]=lsb, [2]=msb -> but Solaar does pair_info[3:4]+pair_info[2:3] which puts MSB byte FIRST then LSB)
        //   ble address bytes follow, protocol byte further on.
        // Be defensive — fields stay default if the reply layout is short.
        var p = reply.Value.Parameters.Span;
        var device = EnsureSlot(slot);

        if (p.Length >= 4)
        {
            // Solaar swaps bytes 2..3: extract_wpid(pair_info[3:4] + pair_info[2:3]).
            // That yields wpid = (p[3] << 8) | p[2]. Match the 0xB034 we get from 0x41.
            var wpid = (ushort)((p[3] << 8) | p[2]);
            if (wpid != 0)
                device.Wpid = wpid;
        }
        if (p.Length >= 11)
        {
            // BLE address: 6 bytes starting after wpid + small header. Solaar's
            // extraction varies by firmware; we capture the most-likely span.
            var ble = new byte[6];
            p.Slice(4, 6).CopyTo(ble);
            device.HostIdentifier = ble;
        }
        if (p.Length >= 12)
        {
            device.ProtocolVersion = p[10];
        }
        RefreshSlot(slot);
        return true;
    }

    /// <summary>
    /// Convenience: reads both BOLT_DEVICE_NAME and BOLT_PAIRING_INFORMATION
    /// for a slot in one call. Tolerates either failing — successful fields
    /// are populated and the other stays at its prior value.
    /// </summary>
    public async Task ReadSlotMetadataAsync(byte slot, CancellationToken ct = default)
    {
        await ReadSlotPairingInfoAsync(slot, ct).ConfigureAwait(false);
        await ReadSlotNameAsync(slot, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads receiver-level metadata: firmware version, max devices, serial,
    /// and BLE address. Issues HID++ 1.0 register reads to <c>BOLT_UNIQUE_ID</c>
    /// (0xFB) and <c>RECEIVER_INFO</c> (0xB5) sub-registers.
    /// </summary>
    /// <remarks>
    /// All fields are best-effort — any individual read that times out or
    /// errors leaves its field at the default value. The whole call never throws
    /// for HID++ failures; only the HID transport throwing surfaces.
    /// </remarks>
    public async Task<ReceiverDetails> GetReceiverDetailsAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var window = timeout ?? TimeSpan.FromMilliseconds(750);

        string? serial = null;
        var fwMajor = (byte)0;
        var fwMinor = (byte)0;
        var fwBuild = (ushort)0;
        var maxDevices = (byte)0;
        byte[]? ble = null;

        var uniqueId = await Hidpp10ReadAsync(HidPp10.BuildReadBoltUniqueIdFrame(),
            expectedRegister: HidPp10.RegisterBoltUniqueId, window, ct).ConfigureAwait(false);
        if (uniqueId is { } u)
        {
            // BOLT_UNIQUE_ID reply: first 6 bytes are the serial as printable ASCII
            // (e.g. "CEB26A"). Fall back to hex if any byte is non-printable.
            var span = u.Parameters.Span;
            var serialBytes = span[..Math.Min(6, span.Length)];
            var allPrintable = true;
            foreach (var b in serialBytes)
            {
                if (b is < 0x20 or > 0x7E) { allPrintable = false; break; }
            }
            serial = allPrintable
                ? System.Text.Encoding.ASCII.GetString(serialBytes).TrimEnd('\0', ' ')
                : Convert.ToHexString(serialBytes);
        }

        var info = await Hidpp10ReadAsync(
            HidPp10.BuildReadReceiverInfoFrame(HidPp10.InfoSubRegisterReceiverInformation),
            expectedRegister: HidPp10.RegisterReceiverInfo, window, ct).ConfigureAwait(false);
        if (info is { } i)
        {
            // RECEIVER_INFO 0x03 reply layout (per Solaar receiver.py + base.py extract logic):
            //   [0] sub-register echo (0x03)
            //   [1..6] receiver BLE address (6 bytes, MSB first)
            //   [7..9] firmware version "vX.YY.BZZZZ" raw bytes? — Solaar parses as
            //          bytes[3..5] big-endian fw_build, etc.
            // We capture what we can; fields stay default on parse failure.
            var p = i.Parameters.Span;
            if (p.Length >= 7)
                ble = p.Slice(1, 6).ToArray();
            // Solaar's _extract_firmware_version reads sub-register 0x02; sub-register 0x03 carries the BLE address.
            // For firmware version specifically Solaar uses InfoSubRegister 0x02. We'll do a second read.
        }

        // Solaar also queries InfoSubRegister 0x02 for firmware version.
        var fwReply = await Hidpp10ReadAsync(
            HidPp10.BuildReadReceiverInfoFrame(subRegister: 0x02),
            expectedRegister: HidPp10.RegisterReceiverInfo, window, ct).ConfigureAwait(false);
        if (fwReply is { } fw)
        {
            // Sub-register 0x02 reply: [0]=0x02, [1]=entityIdx, [2]=major(BCD), [3]=minor(BCD), [4..5]=build BE
            var p = fw.Parameters.Span;
            if (p.Length >= 6)
            {
                fwMajor = p[2];
                fwMinor = p[3];
                fwBuild = (ushort)((p[4] << 8) | p[5]);
            }
        }

        // Bolt receivers always have 6 device slots. Solaar reads max_devices from product_info, but we hardcode here.
        maxDevices = HidPpConstants.DeviceIndexLastSlot;

        // Stash the BLE address on the receiver so SwitcherService can match
        // against devices' HostBindings without re-reading on every press.
        if (ble is not null)
            HostIdentifier = ble;

        var details = new ReceiverDetails(serial, fwMajor, fwMinor, fwBuild, maxDevices, ble);
        LastKnownDetails = details;
        return details;
    }

    /// <summary>
    /// Sends a HID++ 1.0 register request and awaits the matching reply on the
    /// inbound stream. Matches by device_index 0xFF + (echo of sub_id, register)
    /// or by error reply (sub_id 0x8F with original register).
    /// </summary>
    private async Task<HidPpFrame?> Hidpp10ReadAsync(HidPpFrame request, byte expectedRegister, TimeSpan window, CancellationToken ct)
    {
        var ack = _connection.InboundFrames
            .Where(f => f.DeviceIndex == HidPpConstants.DeviceIndexReceiver)
            .Where(f =>
            {
                // Echo / success: same sub_id + register byte (in our model: FeatureIndex == subId, FunctionAndSwId == register).
                if (f.FeatureIndex == request.FeatureIndex && f.FunctionAndSwId == expectedRegister)
                    return true;
                // HID++ 1.0 error: sub_id 0x8F, then original sub_id (Parameters[0]), original register (Parameters[1])
                if (f.FeatureIndex == 0x8F && f.FunctionAndSwId == request.FeatureIndex)
                {
                    if (f.Parameters.Span.Length > 0 && f.Parameters.Span[0] == expectedRegister)
                        return true;
                }
                return false;
            })
            .FirstAsync()
            .ToTask(ct);

        _client.SendOneWay(request);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(window);
        try
        {
            var reply = await ack.WaitAsync(cts.Token).ConfigureAwait(false);
            if (reply.FeatureIndex == 0x8F)
            {
                _logger.LogDebug("HID++ 1.0 read of register 0x{Reg:X2} returned error", expectedRegister);
                return null;
            }
            return reply;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("HID++ 1.0 read of register 0x{Reg:X2} timed out", expectedRegister);
            return null;
        }
    }

    /// <summary>
    /// "Tap to identify": for <paramref name="window"/> (default 5 seconds),
    /// diverts every divertable reprogrammable control on the slot and surfaces
    /// the first observed press as a confirmation. The CID divert state is
    /// restored to its prior values when the window closes (or on
    /// cancellation), so the device behaves normally afterwards.
    /// </summary>
    /// <returns>
    /// The CID of the first pressed control (e.g. 0x00D2 for Easy-Switch 2),
    /// or null on timeout.
    /// </returns>
    public async Task<ushort?> IdentifyAsync(byte deviceIndex, TimeSpan? window = null, CancellationToken ct = default)
    {
        var w = window ?? TimeSpan.FromSeconds(5);
        var device = TryGetDevice(deviceIndex);
        if (device is null || !device.LinkUp || device.ReprogControlsIndex is not { } reprogIndex)
            return null;

        var controls = await ReprogControls.ListControlsAsync(deviceIndex, reprogIndex, ct).ConfigureAwait(false);
        var divertable = controls.Where(c => c.IsDivertable).ToArray();
        if (divertable.Length == 0)
            return null;

        var originalDivert = device.DivertedHostSwitchCids.ToHashSet();

        // Divert every divertable control for the duration.
        foreach (var c in divertable)
        {
            try { await ReprogControls.SetCidReportingAsync(deviceIndex, reprogIndex, c.ControlId, ReprogControlsService.DivertModes.Diverted, ct); }
            catch { /* best effort */ }
        }

        try
        {
            var press = await _hostSwitchPressed
                .Where(p => p.DeviceIndex == deviceIndex && p.PrimaryCid is not null)
                .FirstAsync()
                .ToTask(ct)
                .WaitAsync(w, ct)
                .ConfigureAwait(false);

            return press.PrimaryCid;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            // Restore CIDs that weren't originally diverted to Normal; leave originals diverted.
            foreach (var c in divertable)
            {
                if (originalDivert.Contains(c.ControlId)) continue;
                try { await ReprogControls.SetCidReportingAsync(deviceIndex, reprogIndex, c.ControlId, ReprogControlsService.DivertModes.Normal, CancellationToken.None); }
                catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Unpairs every populated slot on the receiver. Returns the number of
    /// slots successfully unpaired. Sequential, not parallel — receiver
    /// firmware often serialises pairing-register writes anyway.
    /// </summary>
    public async Task<int> ClearAllPairingsAsync(TimeSpan? perSlotTimeout = null, CancellationToken ct = default)
    {
        // Snapshot the cache so we don't iterate while it mutates.
        var slots = _devicesCache.Items.Select(d => d.DeviceIndex).OrderBy(s => s).ToArray();
        _logger.LogInformation("Clearing all pairings on receiver {Serial}: {Count} slot(s)", Info.Serial, slots.Length);

        var cleared = 0;
        foreach (var slot in slots)
        {
            try
            {
                if (await UnpairAsync(slot, perSlotTimeout, ct).ConfigureAwait(false))
                    cleared++;
            }
            catch (HidPpException ex)
            {
                _logger.LogWarning(ex, "Unpair slot {Slot} failed during clear", slot);
            }
        }

        _logger.LogInformation("Cleared {Cleared}/{Total} slots on receiver {Serial}", cleared, slots.Length, Info.Serial);
        return cleared;
    }

    /// <summary>
    /// Writes a new ASCII name to <c>BOLT_DEVICE_NAME</c> for the given slot.
    /// Up to 14 ASCII chars. Mutates the cached <see cref="PairedDevice.Name"/>
    /// on success.
    /// </summary>
    /// <remarks>
    /// <b>Known limitation</b>: no working rename path exists on tested Bolt
    /// hardware. We try in order:
    /// <list type="number">
    ///   <item><c>0x0007 DEVICE_FRIENDLY_NAME setFriendlyName</c> — succeeds at
    ///         the wire level but firmware silently ignores it (task #33).</item>
    ///   <item><c>BOLT_DEVICE_NAME</c> via HID++ 1.0 SET_LONG_REGISTER — rejected
    ///         with <c>InvalidArgument</c>.</item>
    /// </list>
    /// <c>0x0005 DEVICE_NAME</c> exposes only read functions (count / read /
    /// type) per the HID++ 2.0 spec — there is no setName. A wire-trace of
    /// Logi Options+ doing a rename would be the next investigation.
    /// </remarks>
    /// <returns>True if the rename succeeded (either via device-side
    /// FRIENDLY_NAME or receiver-side BOLT_DEVICE_NAME); throws on error reply; false on timeout.</returns>
    public async Task<bool> RenameDeviceAsync(byte deviceIndex, string newName, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        // Prefer the device-side path (HID++ 2.0 feature 0x0007 setFriendlyName)
        // when available — this is what Logi Options+ uses and the only path
        // current Bolt firmware actually accepts.
        var cached = TryGetDevice(deviceIndex);
        if (cached is { LinkUp: true, DeviceFriendlyNameIndex: { } friendlyIndex })
        {
            try
            {
                _logger.LogInformation("Renaming slot {Slot} via DEVICE_FRIENDLY_NAME (feature 0x0007) to {Name}", deviceIndex, newName);
                await DeviceFriendlyName.SetFriendlyNameAsync(deviceIndex, friendlyIndex, newName, ct).ConfigureAwait(false);
                cached.FriendlyName = newName;
                cached.Name = newName;
                RefreshSlot(deviceIndex);
                return true;
            }
            catch (HidPpException ex)
            {
                _logger.LogWarning(ex, "DEVICE_FRIENDLY_NAME setName rejected; falling back to BOLT_DEVICE_NAME write");
                // fall through to legacy path
            }
        }

        // Legacy / fallback: SET_LONG_REGISTER on BOLT_DEVICE_NAME. Current
        // Bolt firmware tends to reject this with InvalidArgument; we try
        // anyway so we get a meaningful error code surfaced for devices we
        // haven't established the friendly-name path on yet.
        var window = timeout ?? TimeSpan.FromSeconds(2);
        var request = HidPp10.BuildWriteBoltDeviceNameFrame(deviceIndex, newName);

        // Receiver echoes SET_LONG_REGISTER on success.
        var ack = _connection.InboundFrames
            .Where(f => f.DeviceIndex == HidPpConstants.DeviceIndexReceiver)
            .Where(f =>
                (f.FeatureIndex == HidPp10.SubIdSetLongRegister && f.FunctionAndSwId == HidPp10.RegisterReceiverInfo)
                || (f.FeatureIndex == 0x8F && f.FunctionAndSwId == HidPp10.SubIdSetLongRegister))
            .FirstAsync()
            .ToTask(ct);

        _logger.LogInformation("Renaming slot {Slot} to {Name}", deviceIndex, newName);
        _client.SendOneWay(request);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(window);
        try
        {
            var reply = await ack.WaitAsync(cts.Token).ConfigureAwait(false);
            if (reply.FeatureIndex == 0x8F)
            {
                var errorCode = reply.Parameters.Span.Length > 1
                    ? (HidPpErrorCode)reply.Parameters.Span[1]
                    : HidPpErrorCode.Unknown;
                _logger.LogWarning("Rename slot {Slot} returned error {Code}", deviceIndex, errorCode);
                throw new HidPpException(deviceIndex, HidPp10.RegisterReceiverInfo, function: 0, errorCode);
            }

            var device = EnsureSlot(deviceIndex);
            device.Name = newName;
            RefreshSlot(deviceIndex);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Rename slot {Slot} timed out after {Ms} ms", deviceIndex, window.TotalMilliseconds);
            return false;
        }
    }

    /// <summary>
    /// Unpairs a device from the receiver, removing the slot's stored pairing.
    /// Reaches a Bolt receiver's flash via <c>SET_LONG_REGISTER 0x82 BOLT_PAIRING(0xC1)
    /// subaction 0x03</c>. Awaits the receiver's echo (success) or an error
    /// reply within <paramref name="timeout"/>.
    /// </summary>
    /// <returns>True if the receiver acknowledged the unpair, false on timeout.</returns>
    /// <exception cref="HidPpException">Thrown if the receiver returns an error reply.</exception>
    public async Task<bool> UnpairAsync(byte deviceIndex, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var window = timeout ?? TimeSpan.FromSeconds(2);

        // Watch for an echo (sub-id 0x82, register 0xC1) or error (sub-id 0x8F).
        var ack = _connection.InboundFrames
            .Where(f => f.DeviceIndex == HidPpConstants.DeviceIndexReceiver
                        && ((f.FeatureIndex == HidPp10.SubIdSetLongRegister && f.FunctionAndSwId == HidPp10.RegisterBoltPairing)
                            || f.FeatureIndex == 0x8F))
            .FirstAsync()
            .ToTask(ct);

        _logger.LogInformation("Unpairing slot {Slot} on receiver {Serial}", deviceIndex, Info.Serial);
        _client.SendOneWay(HidPp10.BuildBoltUnpairFrame(deviceIndex));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(window);
        try
        {
            var reply = await ack.WaitAsync(cts.Token).ConfigureAwait(false);

            if (reply.FeatureIndex == 0x8F)
            {
                // HID++ 1.0 error reply: byte 3 = original sub-id, byte 4 = original register,
                // byte 5 = error code. In our frame model that maps to FunctionAndSwId,
                // Parameters[0], Parameters[1] respectively.
                var errorCode = reply.Parameters.Span.Length > 1
                    ? (HidPpErrorCode)reply.Parameters.Span[1]
                    : HidPpErrorCode.Unknown;
                _logger.LogWarning("Unpair slot {Slot} returned error {Code}", deviceIndex, errorCode);
                throw new HidPpException(deviceIndex, HidPp10.RegisterBoltPairing, function: 0, errorCode);
            }

            _devicesCache.RemoveKey(deviceIndex);
            _logger.LogInformation("Slot {Slot} unpaired", deviceIndex);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Unpair slot {Slot} timed out after {Ms} ms", deviceIndex, window.TotalMilliseconds);
            return false;
        }
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
