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

    /// <summary>Accessor returning the latest UDP announcement from each known peer (null = topology off).</summary>
    public Func<IEnumerable<LogiPlusSwitcher.Core.Topology.ReceiverAnnouncement>>? PeerAnnouncementsProvider { get; set; }

    /// <summary>Accessor returning per-peer stats from <see cref="LogiPlusSwitcher.Core.Topology.UdpTopologyService.PeerSnapshot"/>.</summary>
    public Func<IEnumerable<LogiPlusSwitcher.Core.Topology.PeerStats>>? PeerStatsProvider { get; set; }

    /// <summary>Accessor returning send-side (attempts, errors) for the diagnostics panel.</summary>
    public Func<(long Attempts, long Errors)>? SendStatsProvider { get; set; }

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
        RefreshDiagnosticsTab();
        RefreshHotkeysTab();
        RefreshNetworkTab();
        Populate();
        WireLiveRefresh();
        StartDiagnosticsTimer();
    }

    public Action? HotkeysChanged { get; set; }
    public Action? TopologyChanged { get; set; }

    private bool _suppressHotkeysEvent;
    private bool _suppressTopologyEvent;
    private readonly ObservableCollection<HotkeyBindingRow> _hotkeyRows = new();

    private void RefreshHotkeysTab()
    {
        if (_settings is null) return;
        var on = this.FindControl<CheckBox>("HotkeysEnabledToggle");
        if (on is not null)
        {
            _suppressHotkeysEvent = true;
            on.IsChecked = _settings.Hotkeys.Enabled;
            _suppressHotkeysEvent = false;
        }

        var list = this.FindControl<ItemsControl>("HotkeyBindingsList");
        if (list is not null && list.ItemsSource is null) list.ItemsSource = _hotkeyRows;

        var targets = DiscoverTargets();
        _hotkeyRows.Clear();
        foreach (var b in _settings.Hotkeys.Bindings)
        {
            var row = new HotkeyBindingRow
            {
                Chord = b.Chord,
                AvailableTargets = targets,
            };
            row.SelectedTarget = string.IsNullOrEmpty(b.TargetBleHex)
                ? null
                : targets.FirstOrDefault(t => string.Equals(t.BleHex, b.TargetBleHex, StringComparison.OrdinalIgnoreCase));
            _hotkeyRows.Add(row);
        }
        UpdateHotkeyStatus("");
    }

    /// <summary>
    /// Builds the dropdown list. One entry per unique BLE seen across every
    /// device's HostBindings + every attached receiver's own BLE. Friendly
    /// label comes from <see cref="HostBinding.ReceiverName"/> when known,
    /// falls back to short hex.
    /// </summary>
    private List<TargetOption> DiscoverTargets()
    {
        var dict = new Dictionary<string, TargetOption>(StringComparer.OrdinalIgnoreCase);
        if (_manager is null) return dict.Values.ToList();

        // 1. This machine's own receivers + their devices' HostBindings.
        foreach (var r in _manager.Receivers.Items)
        {
            if (r.HostIdentifierKey is { } bleSelf)
            {
                var label = string.IsNullOrEmpty(r.Info.Serial) ? "(this receiver)" : $"this machine ({r.Info.Serial})";
                dict.TryAdd(bleSelf, new TargetOption(bleSelf, label));
            }
            foreach (var d in r.Devices.Items)
            {
                foreach (var binding in d.HostBindings.Values)
                {
                    if (!binding.Paired) continue;
                    if (binding.HostIdentifierKey is not { } bleKey) continue;
                    var label = !string.IsNullOrEmpty(binding.ReceiverName)
                        ? binding.ReceiverName!
                        : (binding.HostIdentifierString ?? bleKey);
                    if (!dict.ContainsKey(bleKey))
                        dict[bleKey] = new TargetOption(bleKey, label);
                }
            }
        }

        // 2. Remote receivers — discovered from inbound UDP announcements.
        // Lets the user pick a peer as a hotkey target even if our own
        // device-side HostBindings haven't enriched yet (Win arm64 emulation
        // sometimes times out the HID++ feature discovery — see task #31).
        if (PeerAnnouncementsProvider is not null)
        {
            try
            {
                foreach (var ann in PeerAnnouncementsProvider())
                {
                    foreach (var entry in ann.Receivers)
                    {
                        if (string.IsNullOrEmpty(entry.HostIdentifierHex)) continue;
                        var key = entry.HostIdentifierHex.ToLowerInvariant();
                        if (dict.ContainsKey(key)) continue;
                        var hostname = string.IsNullOrEmpty(ann.Hostname) ? "peer" : ann.Hostname;
                        var serialPart = string.IsNullOrEmpty(entry.Serial) ? "" : $" · {entry.Serial}";
                        dict[key] = new TargetOption(key, $"{hostname}{serialPart}");
                    }
                }
            }
            catch (Exception) { /* defensive — UI never blocks on topology errors */ }
        }

        return dict.Values.OrderBy(t => t.Display, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void OnHotkeysEnabledChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_suppressHotkeysEvent || _settings is null) return;
        if (sender is not CheckBox cb) return;
        _settings.Hotkeys.Enabled = cb.IsChecked == true;
        _settings.Save();
        HotkeysChanged?.Invoke();
        UpdateHotkeyStatus(_settings.Hotkeys.Enabled ? "Hotkeys on." : "Hotkeys off.");
    }

    private void OnSaveHotkeys(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settings is null) return;

        var bad = new List<string>();
        var newBindings = new List<HotkeyBinding>();
        for (var i = 0; i < _hotkeyRows.Count; i++)
        {
            var row = _hotkeyRows[i];
            var raw = (row.Chord ?? "").Trim();
            var chord = LogiPlusSwitcher.Core.Hotkeys.HotkeyChord.Parse(raw);
            if (!string.IsNullOrEmpty(raw) && !chord.IsValid)
            {
                bad.Add($"Row {i + 1}: '{raw}' is not a valid chord");
                continue;
            }
            newBindings.Add(new HotkeyBinding
            {
                Chord = chord.IsValid ? chord.ToString() : raw,
                TargetBleHex = row.SelectedTarget?.BleHex,
                TargetLabel = row.SelectedTarget?.Display,
            });
        }
        if (bad.Count > 0)
        {
            UpdateHotkeyStatus(string.Join("; ", bad));
            return;
        }
        _settings.Hotkeys.Bindings = newBindings;
        _settings.Save();
        HotkeysChanged?.Invoke();
        UpdateHotkeyStatus("Saved. New chords are active.");
    }

    private void UpdateHotkeyStatus(string text)
    {
        var line = this.FindControl<TextBlock>("HotkeyStatusLine");
        if (line is not null) line.Text = text;
    }

    public sealed class HotkeyBindingRow
    {
        public string Chord { get; set; } = "";
        public List<TargetOption> AvailableTargets { get; set; } = new();
        public TargetOption? SelectedTarget { get; set; }
    }

    public sealed record TargetOption(string BleHex, string Display);

    private void RefreshNetworkTab()
    {
        if (_settings is null) return;
        var on = this.FindControl<CheckBox>("TopologyEnabledToggle");
        if (on is not null)
        {
            _suppressTopologyEvent = true;
            on.IsChecked = _settings.Topology.Enabled;
            _suppressTopologyEvent = false;
        }
        var mid = this.FindControl<TextBlock>("MachineIdLine");
        if (mid is not null) mid.Text = _settings.Topology.MachineId ?? "(generated on first enable)";
        var port = this.FindControl<TextBlock>("TopologyPortLine");
        if (port is not null) port.Text = _settings.Topology.Port.ToString();
        var win = this.FindControl<TextBlock>("TopologyWindowLine");
        if (win is not null) win.Text = $"{_settings.Topology.CorrelationWindowSeconds}s after a local link-lost";
        UpdateTopologyStatus("");
    }

    private void OnTopologyEnabledChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_suppressTopologyEvent || _settings is null) return;
        if (sender is not CheckBox cb) return;
        _settings.Topology.Enabled = cb.IsChecked == true;
        _settings.Save();
        TopologyChanged?.Invoke();
        UpdateTopologyStatus(_settings.Topology.Enabled
            ? "Enabled. UDP socket bound and broadcasting now."
            : "Disabled. UDP socket released.");
    }

    private void UpdateTopologyStatus(string text)
    {
        var line = this.FindControl<TextBlock>("TopologyStatusLine");
        if (line is not null) line.Text = text;
    }

    private bool _suppressTelemetryEvent;

    private void RefreshDiagnosticsTab()
    {
        if (_settings is null) return;
        var t = this.FindControl<CheckBox>("TelemetryToggle");
        if (t is not null)
        {
            _suppressTelemetryEvent = true;
            t.IsChecked = _settings.TelemetryEnabled;
            _suppressTelemetryEvent = false;
        }
        var line = this.FindControl<TextBlock>("LogsPathLine");
        if (line is not null) line.Text = $"Logs: {AppPaths.LogsDirectory}";
    }

    private void OnTelemetryChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_suppressTelemetryEvent || _settings is null) return;
        if (sender is not CheckBox cb) return;
        _settings.TelemetryEnabled = cb.IsChecked == true;
        _settings.Save();
    }

    private void OnOpenLogsFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        RevealInFileManager(AppPaths.LogsDirectory);

    private void OnOpenSettingsFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        RevealInFileManager(System.IO.Path.GetDirectoryName(AppPaths.SettingsFile) ?? AppPaths.LogsDirectory);

    private static void RevealInFileManager(string path)
    {
        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
                System.Diagnostics.Process.Start("open", $"\"{path}\"");
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            else
                System.Diagnostics.Process.Start("xdg-open", $"\"{path}\"");
        }
        catch
        {
            // best-effort; ignore platform-specific failures
        }
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
            if (r.HostIdentifierKey is not null) allBleKeys.Add(r.HostIdentifierKey);
            foreach (var d in r.Devices.Items)
            {
                foreach (var binding in d.HostBindings.Values)
                {
                    if (binding.Paired && binding.HostIdentifierKey is not null)
                        allBleKeys.Add(binding.HostIdentifierKey);
                }
            }
        }

        // Header row.
        var header = new StringBuilder("Device".PadRight(30));
        var bleList = allBleKeys.ToList();
        foreach (var ble in bleList)
        {
            // Label as receiver serial if known, else short BLE.
            var owner = receivers.FirstOrDefault(r => r.HostIdentifierKey == ble);
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
                    var slot = d.FindHostSlotForHostId(ble);
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

    public Action? HostNamesChanged { get; set; }

    private async void OnEditGlobalHostNames(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settings is null) return;
        var result = await Dialogs.HostNamesDialog.AskAsync(
            this,
            title: "Default host labels",
            header: "Host labels (global default)",
            hint: "Used everywhere unless a per-receiver override is set. " +
                  "Empty fields fall back to 'Host N'.",
            initial: _settings.HostNames);
        if (result is null) return;
        var sanitized = new[]
        {
            string.IsNullOrWhiteSpace(result[0]) ? "Host 1" : result[0],
            string.IsNullOrWhiteSpace(result[1]) ? "Host 2" : result[1],
            string.IsNullOrWhiteSpace(result[2]) ? "Host 3" : result[2],
        };
        _settings.HostNames = sanitized;
        _settings.Save();
        HostNamesChanged?.Invoke();
        Populate();
    }

    private async void OnEditReceiverHostNames(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settings is null || sender is not Button btn || btn.Tag is not string serial || string.IsNullOrEmpty(serial)) return;

        _settings.Receivers.TryGetValue(serial, out var rs);
        var initial = rs?.HostNames ?? _settings.HostNames;
        var result = await Dialogs.HostNamesDialog.AskAsync(
            this,
            title: $"Host labels for {serial}",
            header: $"Host labels (override for receiver {serial})",
            hint: "Clear all three to remove the override and fall back to the global defaults.",
            initial: initial);
        if (result is null) return;

        // Empty-all => remove override.
        if (result.All(string.IsNullOrWhiteSpace))
        {
            if (rs is not null) rs.HostNames = null;
        }
        else
        {
            rs ??= new ReceiverSettings();
            rs.HostNames = new[]
            {
                string.IsNullOrWhiteSpace(result[0]) ? "Host 1" : result[0],
                string.IsNullOrWhiteSpace(result[1]) ? "Host 2" : result[1],
                string.IsNullOrWhiteSpace(result[2]) ? "Host 3" : result[2],
            };
            _settings.Receivers[serial] = rs;
        }
        _settings.Save();
        Populate();
    }

    private async void OnClearAllPairings(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string serial || _manager is null) return;
        var receiver = _manager.Receivers.Items.FirstOrDefault(r => r.Info.Serial == serial);
        if (receiver is null) return;

        var ok = await Dialogs.ConfirmDialog.AskAsync(
            this,
            title: "Clear all pairings",
            header: $"Clear ALL pairings on receiver {serial}?",
            body: "Every paired device on this receiver will be unpaired. You'll need to re-pair " +
                  "each one via Logi Options+ or Pair-New-Device. This is not reversible.",
            confirmLabel: "Clear all");
        if (!ok) return;

        var status = this.FindControl<TextBlock>("StatusLine");
        try
        {
            var cleared = await receiver.ClearAllPairingsAsync();
            if (status is not null) status.Text = $"Cleared {cleared} slot(s) on {serial}.";
        }
        catch (Exception ex)
        {
            if (status is not null) status.Text = $"Clear-all failed: {ex.Message}";
        }
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

    // ====================================================================
    // Diagnostics tab — full live snapshot of HID state + network sync
    // ====================================================================

    private DispatcherTimer? _diagTimer;

    private void StartDiagnosticsTimer()
    {
        _diagTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _diagTimer.Tick += (_, _) => RefreshDiagnostics();
        _diagTimer.Start();
        _disposables.Add(System.Reactive.Disposables.Disposable.Create(() =>
        {
            _diagTimer.Stop();
            _diagTimer = null;
        }));
        RefreshDiagnostics();
    }

    private void OnRefreshDiagnostics(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        RefreshDiagnostics();

    private void RefreshDiagnostics()
    {
        var tb = this.FindControl<SelectableTextBlock>("DiagnosticsText");
        if (tb is null) return;
        try { tb.Text = BuildDiagnosticsSnapshot(); }
        catch (Exception ex) { tb.Text = $"(diagnostics render failed: {ex.Message})"; }
    }

    private string BuildDiagnosticsSnapshot()
    {
        var sb = new StringBuilder();
        sb.AppendLine("════════════════════════════════════════════════════════════════════");
        sb.AppendLine($"  LogiPlusSwitcher — diagnostics snapshot at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("════════════════════════════════════════════════════════════════════");
        sb.AppendLine();

        // ---- Local receivers ----
        sb.AppendLine("LOCAL");
        sb.AppendLine("─────");
        if (_manager is null || _manager.Receivers.Count == 0)
        {
            sb.AppendLine("  (no receivers attached)");
        }
        else
        {
            foreach (var r in _manager.Receivers.Items)
                RenderReceiver(sb, r);
        }
        sb.AppendLine();

        // ---- Network sync ----
        sb.AppendLine("NETWORK SYNC");
        sb.AppendLine("────────────");
        if (_settings is null || !_settings.Topology.Enabled)
        {
            sb.AppendLine("  (disabled in Settings → Network)");
        }
        else
        {
            var send = SendStatsProvider?.Invoke();
            if (send is not null)
            {
                var (att, err) = send.Value;
                sb.AppendLine($"  Send attempts: {att,8} | errors: {err,6}{(err > 0 ? " ⚠" : "")}");
            }
            sb.AppendLine($"  Local machineId: {_settings.Topology.MachineId ?? "(none)"}");
            sb.AppendLine($"  UDP port: {_settings.Topology.Port}  |  multicast: {(_settings.Topology.UseMulticast ? _settings.Topology.MulticastGroup : "off")}");
            sb.AppendLine($"  Repeat: {_settings.Topology.RepeatCount}× per announcement, gap {_settings.Topology.RepeatGapMs}ms");
            sb.AppendLine($"  Cadence: normal {_settings.Topology.BroadcastIntervalSeconds}s; burst {_settings.Topology.BurstIntervalMs}ms for {_settings.Topology.BurstDurationMs}ms after link-lost");
            sb.AppendLine();

            var peers = PeerStatsProvider?.Invoke()?.ToList() ?? new List<LogiPlusSwitcher.Core.Topology.PeerStats>();
            var anns = PeerAnnouncementsProvider?.Invoke()?.ToDictionary(a => a.MachineId, a => a)
                       ?? new Dictionary<string, LogiPlusSwitcher.Core.Topology.ReceiverAnnouncement>();

            if (peers.Count == 0 && anns.Count == 0)
            {
                sb.AppendLine("  (no peers discovered yet)");
            }
            else
            {
                foreach (var peer in peers.OrderBy(p => p.Hostname ?? p.MachineId))
                    RenderPeer(sb, peer, anns.TryGetValue(peer.MachineId, out var a) ? a : null);
            }
        }
        sb.AppendLine();

        // ---- Settings summary ----
        sb.AppendLine("SETTINGS");
        sb.AppendLine("────────");
        if (_settings is not null)
        {
            sb.AppendLine($"  License key set: {(!string.IsNullOrEmpty(_settings.LicenseKey) ? "yes" : "no")}");
            sb.AppendLine($"  Hotkeys enabled: {_settings.Hotkeys.Enabled} | bindings: {_settings.Hotkeys.Bindings.Count}");
            foreach (var b in _settings.Hotkeys.Bindings)
                sb.AppendLine($"    • {b.Chord,-22} → {(string.IsNullOrEmpty(b.TargetBleHex) ? "(unbound)" : $"{b.TargetLabel ?? b.TargetBleHex} [{b.TargetBleHex}]")}");
            sb.AppendLine($"  Primary receiver: {_settings.PrimaryReceiverSerial ?? "(none)"}");
            sb.AppendLine($"  Auto-update: {_settings.AutoCheckForUpdates} (every {_settings.UpdateCheckIntervalHours}h)");
            sb.AppendLine($"  Telemetry enabled: {_settings.TelemetryEnabled}");
        }

        return sb.ToString();
    }

    private static void RenderReceiver(StringBuilder sb, BoltReceiver r)
    {
        var details = r.LastKnownDetails;
        var fw = details is not null
            ? $"fw {details.FirmwareVersionString}"
            : "fw (not read)";
        var serial = !string.IsNullOrEmpty(r.Info.Serial) ? r.Info.Serial
                    : details?.Serial ?? "(no serial)";
        var part = r.IsParticipating ? "participating" : "STANDBY";
        var ble = r.HostIdentifierKey ?? "(no ble)";

        sb.AppendLine($"  ▼ {r.Info.ProductString}  [{part}]");
        sb.AppendLine($"      serial : {serial}");
        sb.AppendLine($"      {fw}");
        sb.AppendLine($"      ble    : {ble}");
        sb.AppendLine($"      path   : {Shorten(r.Info.Path, 70)}");

        var devices = r.Devices.Items.OrderBy(d => (int)d.DeviceIndex).ToList();
        if (devices.Count == 0)
        {
            sb.AppendLine("      └─ (no paired devices)");
            return;
        }
        foreach (var d in devices)
            RenderDevice(sb, d);
    }

    private static void RenderDevice(StringBuilder sb, PairedDevice d)
    {
        var name = d.DisplayName;
        var link = d.LinkUp ? "● online" : "○ offline";
        var bat = d.LastKnownBattery is { } b
            ? $"{(b.Percent.HasValue ? b.Percent + "%" : "?")}{(b.Charging == true ? "⚡" : "")}"
            : "?";
        var fw = d.Firmware is not null ? d.Firmware.DisplayString : "(fw not read)";
        var current = d.LastKnownCurrentHost.HasValue ? $" current=H{d.LastKnownCurrentHost + 1}" : "";

        sb.AppendLine($"      ├─ Slot {d.DeviceIndex}  {name}  ({link}){current}");
        sb.AppendLine($"      │     wpid    : 0x{d.Wpid:X4}");
        sb.AppendLine($"      │     serial  : {d.Serial ?? "(unknown)"}");
        sb.AppendLine($"      │     battery : {bat}");
        sb.AppendLine($"      │     {fw}");
        if (d.HostBindings.Count == 0)
        {
            sb.AppendLine("      │     hosts   : (not yet read)");
            return;
        }
        sb.AppendLine("      │     hosts:");
        foreach (var (slot, binding) in d.HostBindings.OrderBy(kv => kv.Key))
        {
            var marker = d.LastKnownCurrentHost == slot ? " ← current" : "";
            if (!binding.Paired)
            {
                sb.AppendLine($"      │       H{slot + 1}: unpaired{marker}");
                continue;
            }
            var bleKey = binding.HostIdentifierKey ?? "(no ble)";
            var rname = !string.IsNullOrEmpty(binding.ReceiverName) ? $" ({binding.ReceiverName})" : "";
            sb.AppendLine($"      │       H{slot + 1}: {bleKey}{rname}{marker}");
        }
    }

    private static void RenderPeer(StringBuilder sb,
        LogiPlusSwitcher.Core.Topology.PeerStats peer,
        LogiPlusSwitcher.Core.Topology.ReceiverAnnouncement? lastAnn)
    {
        var hostname = string.IsNullOrEmpty(peer.Hostname) ? "(unknown host)" : peer.Hostname;
        var sinceMs = (DateTime.UtcNow - peer.LastSeenUtc).TotalMilliseconds;
        var sinceStr = peer.LastSeenUtc == default
            ? "never"
            : sinceMs < 1500 ? $"{sinceMs:F0}ms ago"
                             : $"{sinceMs / 1000:F1}s ago";
        var alive = sinceMs < 10_000 ? "✓" : "⚠ silent";

        sb.AppendLine($"  ▼ {hostname}  [{alive}]");
        sb.AppendLine($"      machineId : {peer.MachineId}");
        sb.AppendLine($"      last seen : {sinceStr}");
        sb.AppendLine($"      last seq  : {peer.LastSeq}");
        sb.AppendLine($"      received  : {peer.UniqueReceived} unique / {peer.DuplicatesSuppressed} dup");
        sb.AppendLine($"      missed in : {peer.MissedFromPeer}  (peer→us loss, inferred from seq gaps)");
        sb.AppendLine($"      peer ack  : last seq of ours they saw = {peer.LastAckOfOurSeq}");
        sb.AppendLine($"      missed out: {peer.OutboundLossEstimate}  (us→peer loss, inferred from peer ack)");

        if (lastAnn is not null)
        {
            foreach (var r in lastAnn.Receivers)
            {
                var ble = r.HostIdentifierHex ?? "(no ble)";
                var ser = string.IsNullOrEmpty(r.Serial) ? "(no serial)" : r.Serial;
                sb.AppendLine($"      └─ Receiver {ser}  ble={ble}");
                if (r.OnlineDevices.Count == 0) sb.AppendLine("           (no devices online)");
                foreach (var od in r.OnlineDevices)
                    sb.AppendLine($"           ● Slot {od.Slot}  wpid 0x{od.WpidHex}  {od.Name ?? ""}");
            }
        }
    }

    private static string Shorten(string s, int max) =>
        s.Length > max ? string.Concat("…", s.AsSpan(s.Length - max + 1)) : s;
}
