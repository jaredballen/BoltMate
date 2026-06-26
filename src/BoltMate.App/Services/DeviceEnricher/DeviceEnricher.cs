using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading;
using DynamicData;
using BoltMate.Core.Bolt;
using BoltMate.Core.HidPp;
using BoltMate.Core.HidPp.Features;
using BoltMate.Core.Services;
using Microsoft.Extensions.Logging;

namespace BoltMate.App.Services;

/// <summary>
/// Watches every receiver attached via <see cref="ReceiverManager"/> and runs
/// background metadata enrichment when devices link up — feature discovery,
/// device-side name read, battery read. Keeps the tray menu fresh without
/// the user having to click anything.
/// </summary>
public sealed class DeviceEnricher : IDeviceEnricher
{
    private readonly IReceiverManager _manager;
    private readonly ILogger<DeviceEnricher> _logger;
    private readonly CompositeDisposable _disposables = new();
    // One gate per receiver. Serialises enrichment across slots so we don't
    // flood the HID wire with 14+ concurrent requests when two devices link up
    // simultaneously — that race caused slot 1's feature discovery to silently
    // time out while slot 2 succeeded, leaving the keyboard unfit for fan-out.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _receiverGates = new();

    public DeviceEnricher(IReceiverManager manager, ILogger<DeviceEnricher> logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(logger);
        _manager = manager;
        _logger = logger;

        _disposables.Add(_manager.Receivers.Connect().Subscribe(changes =>
        {
            foreach (var change in changes)
            {
                if (change.Reason is ChangeReason.Add)
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

        // On every fresh link-up: resolve features + run heavy reads eagerly.
        // The chunked reads (host friendly names, device name) can race the
        // device's own post-link-up init and return cached defaults; the
        // HostsInfoService retry layer + the binding-merge below cope with
        // most of that.
        _disposables.Add(receiver.LinkEstablished.Subscribe(device =>
            _ = EnrichSlotAsync(receiver, device.DeviceIndex)));

        // 0x1D4B WIRELESS_DEVICE_STATUS notification — device-initiated "I'm
        // ready" signal. We don't BLOCK on it (would be fragile if the
        // notification never arrives) but we DO use it as an upgrade trigger:
        // when it fires, re-run the heavy reads so any stale binding the
        // initial pass got is replaced. Idempotent w.r.t. the per-receiver
        // semaphore so concurrent slots don't trample each other.
        _disposables.Add(receiver.DeviceReady.Subscribe(device =>
            _ = RefreshSlotAsync(receiver, device.DeviceIndex)));
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

            // Per-slot pass. For each possible Bolt slot:
            //   1. Read pairing info from receiver flash (works even if the
            //      device itself is asleep — metadata only, no RF traffic).
            //   2. If a paired device is in this slot but not currently
            //      link-up (DJ_PAIRING hasn't fired since attach), try to
            //      wake it with a single HID++ 2.0 short request. Light-
            //      sleep devices answer; deep-sleep devices time out and
            //      stay marked as paired-but-offline.
            for (byte s = HidPpConstants.DeviceIndexFirstSlot; s <= HidPpConstants.DeviceIndexLastSlot; s++)
            {
                await receiver.ReadSlotMetadataAsync(s);
                await TryWakeSlotAsync(receiver, s);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Receiver enrichment pass surfaced an exception (non-fatal)");
        }
    }

    /// <summary>
    /// If the slot has paired metadata (WPID != 0) but isn't currently
    /// link-up, send a short HID++ 2.0 IRoot request as a wake ping. The
    /// receiver queues the packet on the RF channel; light-sleep devices
    /// pick it up on their next wake window and respond. A successful
    /// response means the device is reachable: we flip <c>LinkUp</c> and
    /// fire the full slot enrichment (features + heavy reads). A failure
    /// (timeout / HidPpException) means deep-sleep — the slot stays
    /// paired-but-offline until physical user input wakes the device,
    /// which triggers a normal DJ_PAIRING link-up notification.
    /// </summary>
    private async System.Threading.Tasks.Task TryWakeSlotAsync(BoltReceiver receiver, byte slot)
    {
        var device = receiver.TryGetDevice(slot);
        if (device is null) return;
        if (device.Wpid == 0) return;           // slot is empty, nothing to wake
        if (device.LinkUp) return;              // already linked, normal flow handles it

        try
        {
            var lookup = await receiver.Root.GetFeatureAsync(slot, FeatureIds.DeviceInfo);
            if (lookup is null)
            {
                // Device responded but reported no DeviceInfo feature.
                // Still a successful ping — slot is reachable.
                _logger.LogInformation("Slot {Slot} ping responded (no DeviceInfo) — marking linked", slot);
            }
            else
            {
                _logger.LogInformation("Slot {Slot} woken via ping — DeviceInfo index 0x{Idx:X2}", slot, lookup.Index);
            }

            device.LinkUp = true;
            receiver.RefreshSlot(slot);
            _ = EnrichSlotAsync(receiver, slot);
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Slot {Slot} wake ping failed — paired but offline ({Reason})",
                slot, ex.GetType().Name);
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
        }
        catch (Exception ex)
        {
            // Discover races device init — IRoot reads can time out if the
            // wireless link is up but firmware hasn't finished its own boot.
            // Bail; the next LinkEstablished (or a future DeviceReady refresh)
            // will retry from scratch.
            _logger.LogWarning(ex, "Resolving features for slot {Slot} failed — will retry on next link transition", deviceIndex);
            return;
        }

        await HeavyEnrichInnerAsync(receiver, deviceIndex).ConfigureAwait(false);
    }

    private async System.Threading.Tasks.Task RefreshSlotAsync(BoltReceiver receiver, byte deviceIndex)
    {
        var gate = _receiverGates.GetOrAdd(receiver.Info.Path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var device = receiver.TryGetDevice(deviceIndex);
            if (device is null) return;

            // If feature discovery failed earlier (e.g. IRoot timed out before
            // the device was ready) we won't have indices yet — try again now
            // that the firmware has explicitly signaled ready.
            if (device.HostsInfoIndex is null)
            {
                try { await receiver.DiscoverFeaturesAsync(deviceIndex); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Slot {Slot} DeviceReady-triggered discover retry failed", deviceIndex);
                    return;
                }
            }

            _logger.LogInformation("Slot {Slot} refresh triggered by WIRELESS_DEVICE_STATUS ready", deviceIndex);
            await HeavyEnrichInnerAsync(receiver, deviceIndex).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async System.Threading.Tasks.Task HeavyEnrichInnerAsync(BoltReceiver receiver, byte deviceIndex)
    {
        try
        {
            var device = receiver.TryGetDevice(deviceIndex);
            if (device is null) return;

            // Guard: HID++ reads against an offline device return garbage
            // (all-unpaired bindings, empty names) which would clobber the
            // last good in-memory state. Better to bail and wait for the
            // next link-up / DeviceReady to re-read live.
            if (!device.LinkUp)
            {
                _logger.LogInformation("Slot {Slot} heavy enrich skipped — LinkUp=false at read time", deviceIndex);
                return;
            }

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

                    // Upgrade-only binding merge: never replace a slot we
                    // previously knew as paired+named with a worse read.
                    // The 0x1815 reads are intermittently flaky — under
                    // rapid link transitions a slot can come back as
                    // Paired=false, or with the generic "Logitech Bolt
                    // receiver" default in place of the real name. Blindly
                    // overwriting wipes our last-known-good state and
                    // strands fan-out (no slot map → can't route by name).
                    var bindings = await receiver.HostsInfo.GetAllHostsAsync(deviceIndex, hostsIdx);
                    var prior = device.HostBindings;
                    var dict = new Dictionary<byte, BoltMate.Core.Bolt.HostBinding>();
                    foreach (var b in bindings)
                    {
                        var merged = b;
                        if (prior.TryGetValue(b.HostIndex, out var prev) && IsBetterBinding(prev, b))
                            merged = prev;

                        // Local-host substitution: the slot equal to the
                        // device's current host is bound to US (the device is
                        // talking to us via this receiver, on this slot index).
                        // Whatever name the device stored for that slot is
                        // moot — the canonical answer is our own host name.
                        // Cleans up the common case where the mouse's h0
                        // friendly name reads back as the receiver default
                        // even though that slot really points at this Mac.
                        if (merged.HostIndex == hosts.CurrentHost && IsGenericName(merged.ReceiverName))
                            merged = merged with { ReceiverName = BoltMate.Core.Topology.LocalHostIdentity.Canonical };

                        dict[b.HostIndex] = merged;
                    }
                    device.HostBindings = dict;

                    static bool IsGenericName(string? name) =>
                        string.IsNullOrWhiteSpace(name)
                        || string.Equals(name, "Logitech Bolt receiver", StringComparison.OrdinalIgnoreCase);

                    // "prior is better than incoming" — keep prior if:
                    //   • incoming is unpaired but prior was paired
                    //   • incoming has generic/empty name but prior had a real one
                    static bool IsBetterBinding(BoltMate.Core.Bolt.HostBinding prior, BoltMate.Core.Bolt.HostBinding incoming)
                    {
                        if (prior.Paired && !incoming.Paired) return true;
                        if (prior.Paired && incoming.Paired
                            && !IsGenericName(prior.ReceiverName)
                            && IsGenericName(incoming.ReceiverName)) return true;
                        return false;
                    }

                    var summary = string.Join(", ", bindings.Select(b =>
                        $"h{b.HostIndex}={(b.Paired ? $"{b.HostIdentifierString ?? "(no ble)"}|name='{b.ReceiverName ?? "(null)"}'" : "unpaired")}"));
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
