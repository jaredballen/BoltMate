using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DynamicData;
using LogiPlusSwitcher.App.Licensing;
using LogiPlusSwitcher.Core;
using LogiPlusSwitcher.Core.Bolt;
using Microsoft.Extensions.Logging;

namespace LogiPlusSwitcher.App;

/// <summary>
/// Builds the tray <see cref="NativeMenu"/> from observed receiver + device
/// state. Hooks <see cref="ReceiverManager.Receivers"/> + each receiver's
/// <see cref="BoltReceiver.Devices"/> cache and mutates the menu on the UI
/// thread as devices come and go.
/// </summary>
public sealed class TrayMenuController : IDisposable
{
    private readonly NativeMenu _menu;
    private readonly ReceiverManager _manager;
    private readonly ILicenseService _license;
    private readonly AppSettings? _settings;
    private readonly ILogger<TrayMenuController> _logger;
    private readonly CompositeDisposable _disposables = new();
    private readonly Dictionary<string, ReceiverSection> _sections = new();

    private NativeMenuItem _switchHost1 = null!;
    private NativeMenuItem _switchHost2 = null!;
    private NativeMenuItem _switchHost3 = null!;
    private NativeMenuItem _countItem = null!;
    private NativeMenuItem _settingsItem = null!;
    private NativeMenuItem _updatesItem = null!;
    private NativeMenuItem _aboutItem = null!;
    private NativeMenuItem _quitItem = null!;
    private NativeMenuItemSeparator _dynamicSectionEnd = null!;

    public TrayMenuController(
        NativeMenu menu,
        ReceiverManager manager,
        ILicenseService license,
        ILogger<TrayMenuController> logger,
        AppSettings? settings = null)
    {
        _menu = menu;
        _manager = manager;
        _license = license;
        _logger = logger;
        _settings = settings;

        BuildStaticItems();
        WireReceiverSubscriptions();
    }

    /// <summary>
    /// Re-reads <see cref="AppSettings.HostNames"/> and refreshes the
    /// "Switch all to X" labels. Call after Settings persists a label edit.
    /// </summary>
    public void RefreshHostLabels()
    {
        var (n1, n2, n3) = ResolveHostNames();
        Dispatcher.UIThread.Post(() =>
        {
            _switchHost1.Header = $"Switch all to {n1}";
            _switchHost2.Header = $"Switch all to {n2}";
            _switchHost3.Header = $"Switch all to {n3}";
        });
    }

    private (string, string, string) ResolveHostNames()
    {
        var names = _settings?.HostNames;
        return (
            names is not null && names.Length > 0 ? names[0] : "Host 1",
            names is not null && names.Length > 1 ? names[1] : "Host 2",
            names is not null && names.Length > 2 ? names[2] : "Host 3");
    }

    public void Dispose() => _disposables.Dispose();

    private void BuildStaticItems()
    {
        _menu.Items.Clear();

        var (n1, n2, n3) = ResolveHostNames();
        _switchHost1 = new NativeMenuItem($"Switch all to {n1}");
        _switchHost2 = new NativeMenuItem($"Switch all to {n2}");
        _switchHost3 = new NativeMenuItem($"Switch all to {n3}");
        _switchHost1.Click += (_, _) => SwitchAllTo(0);
        _switchHost2.Click += (_, _) => SwitchAllTo(1);
        _switchHost3.Click += (_, _) => SwitchAllTo(2);

        _countItem = new NativeMenuItem("No receivers attached") { IsEnabled = false };

        _settingsItem = new NativeMenuItem("Settings…");
        _settingsItem.Click += (_, _) => OnSettingsClicked?.Invoke();

        _updatesItem = new NativeMenuItem("Check for updates…");
        _updatesItem.Click += (_, _) => OnCheckForUpdatesClicked?.Invoke();

        _aboutItem = new NativeMenuItem("About LogiPlusSwitcher");
        _aboutItem.Click += (_, _) => OnAboutClicked?.Invoke();

        _quitItem = new NativeMenuItem("Quit");
        _quitItem.Click += (_, _) =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        };

        _dynamicSectionEnd = new NativeMenuItemSeparator();

        _menu.Items.Add(_switchHost1);
        _menu.Items.Add(_switchHost2);
        _menu.Items.Add(_switchHost3);
        _menu.Items.Add(new NativeMenuItemSeparator());
        // dynamic receiver subsections get inserted before _dynamicSectionEnd
        _menu.Items.Add(_dynamicSectionEnd);
        _menu.Items.Add(_countItem);
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(_updatesItem);
        _menu.Items.Add(_aboutItem);
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(_quitItem);
    }

    public Action? OnSettingsClicked { get; set; }
    public Action? OnCheckForUpdatesClicked { get; set; }
    public Action? OnAboutClicked { get; set; }

    private void WireReceiverSubscriptions()
    {
        _disposables.Add(_manager.Receivers.Connect()
            .Subscribe(changes => Dispatcher.UIThread.Post(() =>
            {
                foreach (var change in changes)
                {
                    switch (change.Reason)
                    {
                        case ChangeReason.Add:
                            AddReceiverSection(change.Current);
                            break;
                        case ChangeReason.Remove:
                            RemoveReceiverSection(change.Key);
                            break;
                    }
                }
                UpdateCountItem();
            })));

        _disposables.Add(_license.IsProChanges
            .Subscribe(_ => Dispatcher.UIThread.Post(RefreshAllProGating)));
    }

    private void AddReceiverSection(BoltReceiver receiver)
    {
        if (_sections.ContainsKey(receiver.Info.Path))
            return;

        var section = new ReceiverSection(receiver, _license, _logger);
        _sections[receiver.Info.Path] = section;

        // Insert section items immediately before _dynamicSectionEnd.
        var insertIndex = _menu.Items.IndexOf(_dynamicSectionEnd);
        foreach (var item in section.RootItems)
            _menu.Items.Insert(insertIndex++, item);

        // Subscribe per-receiver device changes to mutate the section's submenu.
        section.WireDeviceSubscription(_disposables);
        UpdateCountItem();
    }

    private void RemoveReceiverSection(string path)
    {
        if (!_sections.Remove(path, out var section))
            return;
        foreach (var item in section.RootItems)
            _menu.Items.Remove(item);
        section.Dispose();
        UpdateCountItem();
    }

    private void RefreshAllProGating()
    {
        foreach (var section in _sections.Values)
            section.RefreshProGating();
    }

    private void UpdateCountItem()
    {
        var receivers = _sections.Count;
        var devices = _sections.Values.Sum(s => s.DeviceCount);
        _countItem.Header = receivers == 0
            ? "No receivers attached"
            : $"{receivers} receiver{(receivers == 1 ? "" : "s")} · {devices} device{(devices == 1 ? "" : "s")}";
    }

    private void SwitchAllTo(byte targetHost)
    {
        foreach (var receiver in _manager.Receivers.Items)
        {
            foreach (var device in receiver.Devices.Items)
            {
                if (device.CanReceiveHostSwitch)
                    receiver.TrySwitchHost(device.DeviceIndex, targetHost);
            }
        }
    }

    /// <summary>One receiver's chunk of the tray menu — its label + per-slot subentries.</summary>
    private sealed class ReceiverSection : IDisposable
    {
        private readonly BoltReceiver _receiver;
        private readonly ILicenseService _license;
        private readonly ILogger _logger;
        private readonly CompositeDisposable _subDisposables = new();
        private readonly Dictionary<byte, NativeMenuItem> _slotItems = new();
        private readonly NativeMenuItem _headerItem;
        private readonly NativeMenuItem _clearAllItem;
        private readonly NativeMenuItemSeparator _trailingSeparator;

        public NativeMenuItem[] RootItems { get; }
        public int DeviceCount => _slotItems.Count;

        public ReceiverSection(BoltReceiver receiver, ILicenseService license, ILogger logger)
        {
            _receiver = receiver;
            _license = license;
            _logger = logger;

            _headerItem = new NativeMenuItem(FormatReceiverHeader(receiver)) { IsEnabled = false };
            // Re-label when the receiver's participation changes (e.g. user
            // picks a different primary in Free mode).
            _subDisposables.Add(receiver.ParticipationChanges.Subscribe(_ =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    _headerItem.Header = FormatReceiverHeader(_receiver))));

            _clearAllItem = new NativeMenuItem("Clear all pairings… (Pro)");
            _clearAllItem.Click += async (_, _) =>
            {
                _logger.LogInformation("User picked Clear all pairings on {Serial}", _receiver.Info.Serial);
                await _receiver.ClearAllPairingsAsync();
            };

            _trailingSeparator = new NativeMenuItemSeparator();

            RootItems =
            [
                _headerItem,
                _clearAllItem,
                _trailingSeparator,
            ];

            RefreshProGating();
        }

        public void WireDeviceSubscription(CompositeDisposable parentDisposables)
        {
            var sub = _receiver.Devices.Connect()
                .Subscribe(changes => Dispatcher.UIThread.Post(() =>
                {
                    foreach (var change in changes)
                    {
                        switch (change.Reason)
                        {
                            case ChangeReason.Add:
                                AddSlot(change.Current);
                                break;
                            case ChangeReason.Remove:
                                RemoveSlot(change.Key);
                                break;
                            case ChangeReason.Refresh or ChangeReason.Update:
                                RefreshSlot(change.Current);
                                break;
                        }
                    }
                }));
            _subDisposables.Add(sub);
            parentDisposables.Add(this);
        }

        public void Dispose() => _subDisposables.Dispose();

        public void RefreshProGating()
        {
            _clearAllItem.IsVisible = _license.IsPro;
            foreach (var slot in _slotItems.Values)
                RefreshSlotProGating(slot);
        }

        private void RefreshSlotProGating(NativeMenuItem slotItem)
        {
            if (slotItem.Menu is null) return;
            foreach (var subItem in slotItem.Menu.Items.OfType<NativeMenuItem>())
            {
                if (subItem.Header?.Contains("(Pro)", StringComparison.Ordinal) == true)
                    subItem.IsVisible = _license.IsPro;
            }
        }

        private void AddSlot(PairedDevice device)
        {
            var item = BuildSlotItem(device);
            _slotItems[device.DeviceIndex] = item;
            InsertSlotItemInOrder(item);
        }

        private void RemoveSlot(byte deviceIndex)
        {
            if (!_slotItems.Remove(deviceIndex, out var item))
                return;
            // Walk owning NativeMenu to remove
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
            {
                var owner = FindMenuContaining(item);
                owner?.Items.Remove(item);
            }
        }

        private void RefreshSlot(PairedDevice device)
        {
            if (!_slotItems.TryGetValue(device.DeviceIndex, out var item))
                return;
            item.Header = FormatSlotHeader(device);
        }

        private NativeMenuItem BuildSlotItem(PairedDevice device)
        {
            var item = new NativeMenuItem(FormatSlotHeader(device));
            var sub = new NativeMenu();

            for (byte h = 0; h <= 2; h++)
            {
                var host = h;
                var switchTo = new NativeMenuItem($"Switch to Host {host + 1}");
                switchTo.Click += (_, _) =>
                {
                    _logger.LogInformation("User switched slot {Slot} -> host {Host}", device.DeviceIndex, host);
                    _receiver.TrySwitchHost(device.DeviceIndex, host);
                };
                sub.Items.Add(switchTo);
            }
            sub.Items.Add(new NativeMenuItemSeparator());

            var identify = new NativeMenuItem("Identify");
            identify.Click += async (_, _) =>
            {
                _logger.LogInformation("User clicked Identify on slot {Slot}", device.DeviceIndex);
                await _receiver.IdentifyAsync(device.DeviceIndex);
            };
            sub.Items.Add(identify);

            sub.Items.Add(new NativeMenuItemSeparator());

            var unpair = new NativeMenuItem("Unpair… (Pro)");
            unpair.Click += async (_, _) =>
            {
                _logger.LogInformation("User unpaired slot {Slot}", device.DeviceIndex);
                await _receiver.UnpairAsync(device.DeviceIndex);
            };
            sub.Items.Add(unpair);

            item.Menu = sub;
            RefreshSlotProGating(item);
            return item;
        }

        private static string FormatSlotHeader(PairedDevice device)
        {
            var status = device.LinkUp ? "online" : "offline";
            var battery = device.LastKnownBattery is { } b && b.Percent.HasValue
                ? $" · {b.Percent}%"
                : "";
            return $"Slot {device.DeviceIndex}: {device.DisplayName} ({status}{battery})";
        }

        private static string FormatReceiverHeader(BoltReceiver receiver)
        {
            var baseLabel = string.IsNullOrEmpty(receiver.Info.Serial)
                ? receiver.Info.ProductString
                : $"{receiver.Info.ProductString} — {receiver.Info.Serial}";
            return receiver.IsParticipating
                ? baseLabel
                : $"{baseLabel}  (standby — Pro)";
        }

        private void InsertSlotItemInOrder(NativeMenuItem item)
        {
            // We rely on NativeMenu items already added by AddReceiverSection.
            // The header lives at index 0, slots in between, then clear/separator.
            // Find the position right before _clearAllItem.
            var owner = FindMenuContaining(_clearAllItem);
            if (owner is null) return;
            var insertAt = owner.Items.IndexOf(_clearAllItem);
            owner.Items.Insert(insertAt, item);
        }

        private static NativeMenu? FindMenuContaining(NativeMenuItemBase item)
        {
            // Avalonia's NativeMenu doesn't expose a parent; we find by walking
            // every TrayIcon menu in the app.
            var trays = TrayIcon.GetIcons(Avalonia.Application.Current!);
            if (trays is null) return null;
            foreach (var tray in trays)
            {
                if (tray.Menu is null) continue;
                if (ContainsRecursive(tray.Menu, item, out var owner))
                    return owner;
            }
            return null;
        }

        private static bool ContainsRecursive(NativeMenu menu, NativeMenuItemBase target, out NativeMenu owner)
        {
            foreach (var it in menu.Items)
            {
                if (ReferenceEquals(it, target))
                {
                    owner = menu;
                    return true;
                }
                if (it is NativeMenuItem nmi && nmi.Menu is not null && ContainsRecursive(nmi.Menu, target, out owner))
                    return true;
            }
            owner = null!;
            return false;
        }
    }
}
