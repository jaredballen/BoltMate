using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading;
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
    // One gate per receiver. Serialises enrichment across slots so we don't
    // flood the HID wire with 14+ concurrent requests when two devices link up
    // simultaneously — that race caused slot 1's feature discovery to silently
    // time out while slot 2 succeeded, leaving the keyboard unfit for fan-out.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _receiverGates = new();

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
            // Receiver-level read for fw version / serial. Await so the result
            // lands on receiver.LastKnownDetails before downstream code (or
            // the diagnostics view) tries to read it.
            try { await receiver.GetReceiverDetailsAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "GetReceiverDetailsAsync failed (non-fatal)"); }

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
        var gate = _receiverGates.GetOrAdd(receiver.Info.Path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnrichSlotInnerAsync(receiver, deviceIndex).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async System.Threading.Tasks.Task EnrichSlotInnerAsync(BoltReceiver receiver, byte deviceIndex)
    {
        try
        {
            await receiver.DiscoverFeaturesAsync(deviceIndex);
            var device = receiver.TryGetDevice(deviceIndex);
            if (device is null) return;

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

            // Firmware version (feature 0x0003 fn 0x1, entity 0 = main).
            if (device.DeviceInfoIndex is { } diIdx)
            {
                try
                {
                    var fw = await receiver.DeviceInfo.GetFirmwareAsync(deviceIndex, diIdx, entityIndex: 0);
                    if (fw is not null)
                    {
                        device.Firmware = fw;
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

            // Divert host-switch buttons so we observe presses BEFORE the
            // device-internal switch executes. Without this, the device just
            // switches itself and we never see the press. SwitcherService
            // listens on HostSwitchPresses and fans out to siblings.
            if (device.ReprogControlsIndex is not null)
            {
                try
                {
                    var diverted = await receiver.DivertHostSwitchCidsAsync(deviceIndex);
                    if (diverted.Count > 0)
                        _logger.LogInformation("Diverted host-switch CIDs on slot {Slot}: [{Cids}]",
                            deviceIndex, string.Join(", ", diverted.Select(c => $"0x{c:X4}")));
                    else
                        _logger.LogInformation("Slot {Slot} exposes no divertable host-switch CIDs (cycle-button device, see #14)",
                            deviceIndex);
                }
                catch (HidPpException ex)
                {
                    _logger.LogWarning("Divert host-switch failed on slot {Slot}: {Err}", deviceIndex, ex.Message);
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

                    var summary = string.Join(", ", bindings.Select(b =>
                        $"h{b.HostIndex}={(b.Paired ? (b.HostIdentifierString ?? "(no ble)") : "unpaired")}"));
                    _logger.LogInformation(
                        "Slot {Slot} HostBindings: current={Current} {Summary}",
                        deviceIndex, hosts.CurrentHost, summary);

                    receiver.RefreshSlot(deviceIndex);
                }
                catch (HidPpException) { /* skip */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Enriching slot {Slot} failed — fan-out will skip this device until a re-enrich pass", deviceIndex);
        }
    }

    public void Dispose()
    {
        _disposables.Dispose();
        foreach (var gate in _receiverGates.Values)
            gate.Dispose();
        _receiverGates.Clear();
    }
}
