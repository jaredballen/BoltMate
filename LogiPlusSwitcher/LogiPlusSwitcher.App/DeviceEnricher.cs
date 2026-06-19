using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using DynamicData;
using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Core.HidPp;
using LogiPlusSwitcher.Core.HidPp.Features;
using Microsoft.Extensions.Logging;

namespace LogiPlusSwitcher.App;

/// <summary>
/// Watches every receiver attached via <see cref="ReceiverManager"/> and runs
/// background metadata enrichment when devices link up — feature discovery,
/// device-side name read, battery read. Keeps the tray menu fresh without
/// the user having to click anything.
/// </summary>
public sealed class DeviceEnricher : IDisposable
{
    private readonly ReceiverManager _manager;
    private readonly ILogger<DeviceEnricher> _logger;
    private readonly CompositeDisposable _disposables = new();

    public DeviceEnricher(ReceiverManager manager, ILogger<DeviceEnricher> logger)
    {
        _manager = manager;
        _logger = logger;

        _disposables.Add(_manager.Receivers.Connect().Subscribe(changes =>
        {
            foreach (var change in changes)
            {
                if (change.Reason == ChangeReason.Add)
                    HookReceiver(change.Current);
            }
        }));
    }

    private void HookReceiver(BoltReceiver receiver)
    {
        // Initial pass: receiver-level + per-slot register reads for any slots
        // already populated (e.g. when the link-up notification fired before
        // our LinkEstablished subscribe was wired up below).
        _ = EnrichAllSlotsAsync(receiver);

        // Also fire the full per-slot enrichment for every slot that's
        // ALREADY linked up at hook time. Otherwise we depend solely on
        // future LinkEstablished events and miss the receiver's first attach.
        foreach (var device in receiver.Devices.Items)
        {
            if (device.LinkUp)
                _ = EnrichSlotAsync(receiver, device.DeviceIndex);
        }

        // On every fresh link-up, run the full enrichment pass for that slot.
        _disposables.Add(receiver.LinkEstablished.Subscribe(device =>
            _ = EnrichSlotAsync(receiver, device.DeviceIndex)));
    }

    private async System.Threading.Tasks.Task EnrichAllSlotsAsync(BoltReceiver receiver)
    {
        try
        {
            // Receiver-level read for fw version / serial.
            _ = receiver.GetReceiverDetailsAsync();

            // Per-slot metadata for every possible Bolt slot — picks up offline
            // devices' names from receiver flash too.
            for (byte s = HidPpConstants.DeviceIndexFirstSlot; s <= HidPpConstants.DeviceIndexLastSlot; s++)
                await receiver.ReadSlotMetadataAsync(s);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Receiver enrichment pass surfaced an exception (non-fatal)");
        }
    }

    private async System.Threading.Tasks.Task EnrichSlotAsync(BoltReceiver receiver, byte deviceIndex)
    {
        try
        {
            await receiver.DiscoverFeaturesAsync(deviceIndex);
            var device = receiver.TryGetDevice(deviceIndex);
            if (device is null) return;

            // DIAGNOSTIC — log the current divert state of every CID so we can
            // diff against pre/post-App-launch snapshots. Tracked under #47/#48.
            _logger.LogInformation("[DIAG-SNAPSHOT] BEGIN slot {Slot}", deviceIndex);
            try
            {
                var notifFlags = await receiver.ReadShortReceiverRegisterAsync(0x00);
                _logger.LogInformation("[DIAG-SNAPSHOT] receiver notification register: bytes=[{Bytes}]",
                    Convert.ToHexString(notifFlags));
                if (device.ReprogControlsIndex is { } reprogIdx)
                {
                    _logger.LogInformation("[DIAG-SNAPSHOT] reprogIndex resolved = 0x{Idx:X2}", reprogIdx);
                    var controls = await receiver.ReprogControls.ListControlsAsync(deviceIndex, reprogIdx);
                    _logger.LogInformation("[DIAG-SNAPSHOT] ListControls returned {Count} CIDs", controls.Count);
                    foreach (var c in controls)
                    {
                        try
                        {
                            var st = await receiver.ReprogControls.GetCidReportingAsync(deviceIndex, reprogIdx, c.ControlId);
                            _logger.LogInformation(
                                "[DIAG-SNAPSHOT] slot {Slot} cid 0x{Cid:X4} flags={Flags} bfield=0x{Bfield:X2} divert={Div} persist={Per} remap=0x{Remap:X4}",
                                deviceIndex, c.ControlId, c.Flags, st.Bfield, st.DivertSet, st.PersistDivertSet, st.RemapControlId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("[DIAG-SNAPSHOT] slot {Slot} cid 0x{Cid:X4} read failed: {Err}", deviceIndex, c.ControlId, ex.Message);
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("[DIAG-SNAPSHOT] slot {Slot} has no ReprogControlsIndex — skipping CID dump", deviceIndex);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DIAG-SNAPSHOT] dump failed for slot {Slot}", deviceIndex);
            }
            _logger.LogInformation("[DIAG-SNAPSHOT] END slot {Slot}", deviceIndex);

            // Device-side name (feature 0x0005) — more accurate than register read.
            if (device.DeviceNameIndex is { } dnIdx)
            {
                try
                {
                    var name = await receiver.DeviceName.GetNameAsync(deviceIndex, dnIdx);
                    if (!string.IsNullOrEmpty(name))
                    {
                        device.Name = name;
                        receiver.RefreshSlot(deviceIndex);
                    }
                }
                catch (HidPpException) { /* skip */ }
            }

            // Friendly name (feature 0x0007) — Logi+ nickname.
            if (device.DeviceFriendlyNameIndex is { } fnIdx)
            {
                try
                {
                    var friendly = await receiver.DeviceFriendlyName.GetAsync(deviceIndex, fnIdx);
                    if (!string.IsNullOrEmpty(friendly))
                    {
                        device.FriendlyName = friendly;
                        receiver.RefreshSlot(deviceIndex);
                    }
                }
                catch (HidPpException) { /* skip */ }
            }

            // Battery level (feature 0x1004).
            if (device.UnifiedBatteryIndex is { } batIdx)
            {
                var bat = await receiver.Battery.GetStatusAsync(deviceIndex, batIdx);
                if (bat is { } b)
                {
                    device.LastKnownBattery = b;
                    receiver.RefreshSlot(deviceIndex);
                }
            }

            // Current host + per-slot BLE bindings (feature 0x1815).
            if (device.HostsInfoIndex is { } hostsIdx)
            {
                try
                {
                    var hosts = await receiver.HostsInfo.GetHostsInfoAsync(deviceIndex, hostsIdx);
                    device.LastKnownCurrentHost = hosts.CurrentHost;

                    // Read each host's binding so SwitcherService can route by BLE.
                    var bindings = await receiver.HostsInfo.GetAllHostsAsync(deviceIndex, hostsIdx);
                    var dict = new Dictionary<byte, LogiPlusSwitcher.Core.Bolt.HostBinding>();
                    foreach (var b in bindings)
                        dict[b.HostIndex] = b;
                    device.HostBindings = dict;

                    receiver.RefreshSlot(deviceIndex);
                }
                catch (HidPpException) { /* skip */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Enriching slot {Slot} surfaced an exception (non-fatal)", deviceIndex);
        }
    }

    public void Dispose() => _disposables.Dispose();
}
