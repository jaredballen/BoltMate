using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using DynamicData;
using LogiPlusSwitcher.App.Licensing;
using LogiPlusSwitcher.App.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using LogiPlusSwitcher.Core;
using LogiPlusSwitcher.Core.Bolt;

namespace LogiPlusSwitcher.App;

/// <summary>
/// Settings UX: per-receiver participation + "Set as primary" controls,
/// a cross-receiver topology matrix, and a license key entry tab.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ReceiverManager? _manager;
    private readonly ReceiverPolicyService? _policy;
    private readonly ILicenseService? _license;
    private readonly AppSettings? _settings;
    private readonly ObservableCollection<ReceiverRow> _rows = new();
    private readonly ObservableCollection<TopologyRow> _topology = new();
    private readonly CompositeDisposable _disposables = new();
    private UpdateService? _updates;

    public SettingsWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _disposables.Dispose();
    }

    public SettingsWindow(
        ReceiverManager manager,
        ReceiverPolicyService policy,
        ILicenseService license,
        AppSettings settings) : this()
    {
        _manager = manager;
        _policy = policy;
        _license = license;
        _settings = settings;

        var list = this.FindControl<ItemsControl>("ReceiverList");
        if (list is not null) list.ItemsSource = _rows;
        var topo = this.FindControl<ItemsControl>("TopologyList");
        if (topo is not null) topo.ItemsSource = _topology;
        var keyBox = this.FindControl<TextBox>("LicenseKeyBox");
        if (keyBox is not null) keyBox.Text = settings.LicenseKey ?? "";

        _updates = new UpdateService(settings, NullLogger<UpdateService>.Instance);

        RefreshLaunchAtLogin();
        RefreshUpdatesTab();
        Populate();
        WireLiveRefresh();
    }

    private void RefreshUpdatesTab()
    {
        if (_updates is null || _settings is null) return;
        var v = this.FindControl<TextBlock>("CurrentVersionLine");
        var lc = this.FindControl<TextBlock>("LastCheckedLine");
        var toggle = this.FindControl<CheckBox>("AutoCheckToggle");
        if (v is not null) v.Text = _updates.CurrentVersion;
        if (lc is not null)
            lc.Text = _updates.LastCheckUtc?.ToLocalTime().ToString("g") ?? "never";
        if (toggle is not null)
        {
            _suppressAutoCheckEvent = true;
            toggle.IsChecked = _settings.AutoCheckForUpdates;
            _suppressAutoCheckEvent = false;
        }
    }

    private bool _suppressAutoCheckEvent;

    private void OnAutoCheckChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_suppressAutoCheckEvent || _settings is null) return;
        if (sender is not CheckBox cb) return;
        _settings.AutoCheckForUpdates = cb.IsChecked == true;
        _settings.Save();
    }

    private async void OnCheckForUpdatesNow(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_updates is null) return;
        var line = this.FindControl<TextBlock>("UpdateStatusLine");
        var btn = this.FindControl<Button>("CheckNowButton");
        if (btn is not null) btn.IsEnabled = false;
        if (line is not null) line.Text = "Checking…";
        try
        {
            var info = await _updates.CheckAsync();
            if (line is not null)
                line.Text = info is null
                    ? $"You're up to date on {_updates.CurrentVersion}."
                    : $"Update available: {info.Version}. Download: {info.DownloadUrl}";
            RefreshUpdatesTab();
        }
        catch (Exception ex)
        {
            if (line is not null) line.Text = $"Update check failed: {ex.Message}";
        }
        finally
        {
            if (btn is not null) btn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Re-Populate() on any receiver attach/detach or per-device cache change
    /// (link state, enrichment, host bindings). Coalesced to 200ms so a burst
    /// of enrichment changes only rebuilds the rows once.
    /// </summary>
    private void WireLiveRefresh()
    {
        if (_manager is null) return;

        var receiversChanged = _manager.Receivers.Connect().Select(_ => System.Reactive.Unit.Default);
        var devicesChanged = _manager.Receivers.Connect()
            .MergeMany(r => r.Devices.Connect())
            .Select(_ => System.Reactive.Unit.Default);

        _disposables.Add(receiversChanged
            .Merge(devicesChanged)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .Subscribe(_ => Dispatcher.UIThread.Post(Populate)));
    }

    private bool _suppressLaunchAtLoginEvent;

    private void RefreshLaunchAtLogin()
    {
        var toggle = this.FindControl<CheckBox>("LaunchAtLoginToggle");
        var detail = this.FindControl<TextBlock>("LaunchAtLoginDetail");
        if (toggle is null) return;

        if (!AppAutostart.CanRegister())
        {
            _suppressLaunchAtLoginEvent = true;
            toggle.IsEnabled = false;
            toggle.IsChecked = false;
            _suppressLaunchAtLoginEvent = false;
            if (detail is not null)
                detail.Text = "Disabled: run from a published build (not 'dotnet run') to enable launch-at-login.";
            return;
        }

        var installed = AppAutostart.IsInstalled();
        _suppressLaunchAtLoginEvent = true;
        toggle.IsChecked = installed;
        toggle.IsEnabled = true;
        _suppressLaunchAtLoginEvent = false;
        if (detail is not null)
            detail.Text = installed
                ? "Registered. LogiPlusSwitcher will start automatically when you log in."
                : "Off. Launch manually from Applications / Start Menu.";
    }

    private void OnLaunchAtLoginChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_suppressLaunchAtLoginEvent) return;
        if (sender is not CheckBox cb) return;

        var detail = this.FindControl<TextBlock>("LaunchAtLoginDetail");
        var want = cb.IsChecked == true;
        var result = want ? AppAutostart.Install() : AppAutostart.Uninstall();
        if (detail is not null) detail.Text = result.Message;
        if (!result.Success)
        {
            _suppressLaunchAtLoginEvent = true;
            cb.IsChecked = !want;
            _suppressLaunchAtLoginEvent = false;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Populate()
    {
        _rows.Clear();
        _topology.Clear();

        var status = this.FindControl<TextBlock>("StatusLine");
        if (_manager is null)
        {
            if (status is not null) status.Text = "No manager wired.";
            return;
        }

        var receivers = _manager.Receivers.Items.ToList();
        if (status is not null)
            status.Text = $"{receivers.Count} receiver{(receivers.Count == 1 ? "" : "s")} attached. " +
                          (_license?.IsPro == true ? "Pro tier — all receivers participate." : "Free tier.");

        var licenseLine = this.FindControl<TextBlock>("LicenseStatusLine");
        if (licenseLine is not null)
            licenseLine.Text = _license?.IsPro == true
                ? "Pro features unlocked. Thanks for supporting development."
                : "Free tier. Enter a license key below to enable Pro features.";

        var multipleReceivers = receivers.Count > 1;
        foreach (var receiver in receivers)
        {
            var slots = receiver.Devices.Items
                .OrderBy(d => (int)d.DeviceIndex)
                .Select(d => new SlotRow
                {
                    Line = $"slot {d.DeviceIndex}  {d.DisplayName}  ({(d.LinkUp ? "online" : "offline")})"
                         + (d.LastKnownBattery is { } b && b.Percent.HasValue ? $"  · {b.Percent}%" : "")
                         + (d.Serial is not null ? $"  · {d.Serial}" : ""),
                    Receiver = receiver,
                    DeviceIndex = d.DeviceIndex,
                    DisplayName = d.DisplayName,
                    LinkUp = d.LinkUp,
                })
                .ToList();

            var (statusLabel, statusBrush, showSetPrimary) = ResolveStatus(receiver, multipleReceivers);

            _rows.Add(new ReceiverRow
            {
                Serial = receiver.Info.Serial,
                Header = receiver.Info.ProductString,
                SubLine = $"{(string.IsNullOrEmpty(receiver.Info.Serial) ? "no serial" : receiver.Info.Serial)} · path: {Shorten(receiver.Info.Path)}",
                StatusLabel = statusLabel,
                StatusBrush = new SolidColorBrush(statusBrush),
                ShowSetPrimary = showSetPrimary,
                Slots = slots,
            });
        }

        BuildTopology(receivers);
    }

    private (string Label, Color Brush, bool ShowSetPrimary) ResolveStatus(BoltReceiver receiver, bool multipleAttached)
    {
        var isPrimary = _settings?.PrimaryReceiverSerial == receiver.Info.Serial;
        if (receiver.IsParticipating)
            return ("ACTIVE", Color.FromRgb(56, 142, 60), false);
        if (multipleAttached && _license?.IsPro == false)
            return ("STANDBY (Pro)", Color.FromRgb(120, 100, 30), !isPrimary);
        return ("OFF", Color.FromRgb(100, 100, 100), false);
    }

    private void BuildTopology(List<BoltReceiver> receivers)
    {
        // Collect all unique BLE addresses referenced by ANY device's HostBindings.
        var allBleKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in receivers)
        {
            if (r.BluetoothAddressKey is not null) allBleKeys.Add(r.BluetoothAddressKey);
            foreach (var d in r.Devices.Items)
            {
                foreach (var binding in d.HostBindings.Values)
                {
                    if (binding.Paired && binding.BluetoothAddressKey is not null)
                        allBleKeys.Add(binding.BluetoothAddressKey);
                }
            }
        }

        // Header row.
        var header = new StringBuilder("Device".PadRight(30));
        var bleList = allBleKeys.ToList();
        foreach (var ble in bleList)
        {
            // Label as receiver serial if known, else short BLE.
            var owner = receivers.FirstOrDefault(r => r.BluetoothAddressKey == ble);
            var label = owner is not null
                ? (owner.Info.Serial.Length > 0 ? owner.Info.Serial : "this rcvr")
                : ble[..Math.Min(6, ble.Length)] + "…";
            header.Append(label.PadRight(14));
        }
        _topology.Add(new TopologyRow { Line = header.ToString() });
        _topology.Add(new TopologyRow { Line = new string('─', Math.Max(30 + bleList.Count * 14, 30)) });

        // Device rows.
        foreach (var r in receivers)
        {
            foreach (var d in r.Devices.Items.OrderBy(d => (int)d.DeviceIndex))
            {
                var row = new StringBuilder($"{d.DisplayName} (slot {d.DeviceIndex})".PadRight(30));
                foreach (var ble in bleList)
                {
                    var slot = d.FindHostSlotForBleKey(ble);
                    row.Append(slot.HasValue ? $"  H{slot.Value + 1}".PadRight(14) : "  -".PadRight(14));
                }
                _topology.Add(new TopologyRow { Line = row.ToString() });
            }
        }

        if (_topology.Count <= 2)
            _topology.Add(new TopologyRow { Line = "(no host bindings yet — devices need to come online once to populate)" });
    }

    private static string Shorten(string path) =>
        path.Length > 60 ? string.Concat("…", path.AsSpan(path.Length - 60)) : path;

    private void OnRefresh(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Populate();

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void OnSetPrimary(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string serial && _policy is not null)
        {
            _policy.SetPrimary(serial);
            Populate();
        }
    }

    private void OnApplyLicense(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var keyBox = this.FindControl<TextBox>("LicenseKeyBox");
        if (_settings is null || keyBox is null) return;

        var key = (keyBox.Text ?? "").Trim();
        _settings.LicenseKey = string.IsNullOrEmpty(key) ? null : key;
        _settings.Save();
        // Real validation lives in task #35. For now this is a no-op — the
        // DevAlwaysProLicenseService doesn't read the key.
        Populate();
    }

    private async void OnIdentifySlot(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SlotRow slot || slot.Receiver is null) return;
        var status = this.FindControl<TextBlock>("StatusLine");
        if (status is not null) status.Text = $"Identifying {slot.DisplayName}: press any key on it within 5s…";
        try
        {
            var cid = await slot.Receiver.IdentifyAsync(slot.DeviceIndex, TimeSpan.FromSeconds(5));
            if (status is not null)
                status.Text = cid.HasValue
                    ? $"Identified {slot.DisplayName} — CID 0x{cid.Value:X4} detected."
                    : $"No tap detected for {slot.DisplayName} within 5s.";
        }
        catch (Exception ex)
        {
            if (status is not null) status.Text = $"Identify failed: {ex.Message}";
        }
    }

    private async void OnRenameSlot(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SlotRow slot || slot.Receiver is null) return;
        var newName = await Dialogs.TextPromptDialog.AskAsync(
            this,
            title: "Rename device",
            prompt: $"New name for {slot.DisplayName} (slot {slot.DeviceIndex})",
            hint: "Stored in the receiver's BOLT_DEVICE_NAME register. Logi Options+ will see the new name.",
            initial: slot.DisplayName);
        if (string.IsNullOrWhiteSpace(newName)) return;

        var status = this.FindControl<TextBlock>("StatusLine");
        try
        {
            var ok = await slot.Receiver.RenameDeviceAsync(slot.DeviceIndex, newName.Trim());
            if (status is not null)
                status.Text = ok ? $"Renamed slot {slot.DeviceIndex} → {newName}" : "Rename failed (firmware rejected).";
        }
        catch (Exception ex)
        {
            if (status is not null) status.Text = $"Rename failed: {ex.Message}";
        }
    }

    private async void OnUnpairSlot(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SlotRow slot || slot.Receiver is null) return;
        var ok = await Dialogs.ConfirmDialog.AskAsync(
            this,
            title: "Unpair device",
            header: $"Unpair {slot.DisplayName}?",
            body: $"Slot {slot.DeviceIndex} on receiver {slot.Receiver.Info.Serial} will be cleared. " +
                  "You'll need to re-pair the device via Logi Options+ or Pair-New-Device.",
            confirmLabel: "Unpair");
        if (!ok) return;

        var status = this.FindControl<TextBlock>("StatusLine");
        try
        {
            var success = await slot.Receiver.UnpairAsync(slot.DeviceIndex);
            if (status is not null)
                status.Text = success ? $"Unpaired slot {slot.DeviceIndex}." : "Unpair failed (firmware rejected).";
        }
        catch (Exception ex)
        {
            if (status is not null) status.Text = $"Unpair failed: {ex.Message}";
        }
    }

    public sealed class ReceiverRow
    {
        public string Serial { get; set; } = "";
        public string Header { get; set; } = "";
        public string SubLine { get; set; } = "";
        public string StatusLabel { get; set; } = "";
        public IBrush StatusBrush { get; set; } = Brushes.Gray;
        public bool ShowSetPrimary { get; set; }
        public List<SlotRow> Slots { get; set; } = new();
    }

    public sealed class SlotRow
    {
        public string Line { get; set; } = "";
        public BoltReceiver? Receiver { get; set; }
        public byte DeviceIndex { get; set; }
        public string DisplayName { get; set; } = "";
        public bool LinkUp { get; set; }
        public bool CanIdentify => LinkUp;
        public bool CanRename => LinkUp;
    }

    public sealed class TopologyRow
    {
        public string Line { get; set; } = "";
    }
}
