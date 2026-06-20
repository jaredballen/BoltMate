using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using LogiPlusSwitcher.Core;
using LogiPlusSwitcher.Core.Bolt;
using Microsoft.Extensions.Logging;

namespace LogiPlusSwitcher.App;

/// <summary>
/// Mirrors every device's <see cref="PairedDevice.HostBindings"/> into
/// <see cref="AppSettings.CachedHostBindings"/> on disk so the topology view
/// can populate instantly on next startup — before any device has had time
/// to wake up and answer feature 0x1815 host reads.
/// </summary>
public sealed class HostBindingPersistence : IDisposable
{
    private readonly ReceiverManager _manager;
    private readonly AppSettings _settings;
    private readonly ILogger<HostBindingPersistence> _logger;
    private readonly CompositeDisposable _disposables = new();

    public HostBindingPersistence(
        ReceiverManager manager,
        AppSettings settings,
        ILogger<HostBindingPersistence> logger)
    {
        _manager = manager;
        _settings = settings;
        _logger = logger;

        HydrateAttachedReceivers();

        // Persist on every device-cache change (link up, metadata read, etc).
        _disposables.Add(_manager.Receivers.Connect()
            .MergeMany(r => r.Devices.Connect())
            .Sample(TimeSpan.FromSeconds(2))
            .Subscribe(_ => SafeFlush()));
    }

    public void Dispose()
    {
        _disposables.Dispose();
        SafeFlush();
    }

    /// <summary>
    /// On startup, copy any persisted bindings back onto each receiver's
    /// EnsureSlot devices so the UI has something to show before the
    /// 0x41/0x1815 enrichment pass completes.
    /// </summary>
    private void HydrateAttachedReceivers()
    {
        if (_settings.CachedHostBindings.Count == 0) return;

        _disposables.Add(_manager.Receivers.Connect().Subscribe(changes =>
        {
            foreach (var change in changes)
            {
                if (change.Reason != ChangeReason.Add) continue;
                Hydrate(change.Current);
            }
        }));
    }

    private void Hydrate(BoltReceiver receiver)
    {
        // Key on serial when present; fall back to HID path. Native Win HID
        // descriptors don't always expose a serial string (we see empty here)
        // so without a path-key fallback the Win-side cache silently never
        // hydrates or persists.
        var key = !string.IsNullOrEmpty(receiver.Info.Serial) ? receiver.Info.Serial : receiver.Info.Path;
        if (!_settings.CachedHostBindings.TryGetValue(key, out var perDevice))
            return;

        foreach (var (slotIdx, slotMap) in perDevice)
        {
            var device = receiver.EnsureSlot(slotIdx);
            if (device.HostBindings.Count > 0) continue; // live data already populated; don't overwrite

            var dict = new Dictionary<byte, HostBinding>();
            foreach (var (hostIdx, persisted) in slotMap)
            {
                byte[]? ble = null;
                if (!string.IsNullOrEmpty(persisted.BluetoothAddressHex))
                {
                    try { ble = Convert.FromHexString(persisted.BluetoothAddressHex); }
                    catch { /* skip bad data */ }
                }
                dict[hostIdx] = new HostBinding(hostIdx, persisted.Paired, ble, persisted.ReceiverName);
            }
            device.HostBindings = dict;
            receiver.RefreshSlot(slotIdx);
        }

        _logger.LogInformation("Hydrated cached host bindings for receiver {Serial}", receiver.Info.Serial);
    }

    private void SafeFlush()
    {
        try
        {
            FlushNow();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HostBinding persistence flush failed (non-fatal)");
        }
    }

    private void FlushNow()
    {
        var snapshot = new Dictionary<string, Dictionary<byte, Dictionary<byte, PersistedHostBinding>>>();
        foreach (var receiver in _manager.Receivers.Items)
        {
            // Match Hydrate(): prefer Serial when present, fall back to Path.
            var key = !string.IsNullOrEmpty(receiver.Info.Serial) ? receiver.Info.Serial : receiver.Info.Path;
            if (string.IsNullOrEmpty(key)) continue;

            var perDevice = new Dictionary<byte, Dictionary<byte, PersistedHostBinding>>();
            foreach (var device in receiver.Devices.Items)
            {
                if (device.HostBindings.Count == 0) continue;
                var slotMap = new Dictionary<byte, PersistedHostBinding>();
                foreach (var (h, binding) in device.HostBindings)
                {
                    slotMap[h] = new PersistedHostBinding
                    {
                        Paired = binding.Paired,
                        BluetoothAddressHex = binding.BluetoothAddress is null
                            ? null
                            : Convert.ToHexString(binding.BluetoothAddress),
                        ReceiverName = binding.ReceiverName,
                    };
                }
                if (slotMap.Count > 0)
                    perDevice[device.DeviceIndex] = slotMap;
            }
            if (perDevice.Count > 0)
                snapshot[key] = perDevice;
        }

        _settings.CachedHostBindings = snapshot;
        _settings.Save();
    }
}
