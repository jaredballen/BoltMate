using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using LogiPlusSwitcher.App.Licensing;
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

    public SettingsWindow()
    {
        InitializeComponent();
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

        RefreshLaunchAtLogin();
        Populate();
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
                         + (d.Serial is not null ? $"  · {d.Serial}" : "")
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
    }

    public sealed class TopologyRow
    {
        public string Line { get; set; } = "";
    }
}
