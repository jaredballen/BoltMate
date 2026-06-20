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
using LogiPlusSwitcher.App.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using LogiPlusSwitcher.Core;
using LogiPlusSwitcher.Core.Bolt;

namespace LogiPlusSwitcher.App;

/// <summary>
/// Settings UX: receivers + paired devices, a cross-receiver topology
/// matrix, and live diagnostics.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ReceiverManager? _manager;
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
        AppSettings settings) : this()
    {
        _manager = manager;
        _settings = settings;

        var list = this.FindControl<ItemsControl>("ReceiverList");
        if (list is not null) list.ItemsSource = _rows;
        var topo = this.FindControl<ItemsControl>("TopologyList");
        if (topo is not null) topo.ItemsSource = _topology;

        _updates = new UpdateService(settings, NullLogger<UpdateService>.Instance);

        RefreshLaunchAtLogin();
        RefreshUpdatesTab();
        RefreshDiagnosticsTab();
        RefreshNetworkTab();
        Populate();
        WireLiveRefresh();
        StartDiagnosticsTimer();
    }

    public Action? TopologyChanged { get; set; }

    private bool _suppressTopologyEvent;

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
            status.Text = $"{receivers.Count} receiver{(receivers.Count == 1 ? "" : "s")} attached.";

        foreach (var receiver in receivers)
        {
            var slots = receiver.Devices.Items
                .OrderBy(d => (int)d.DeviceIndex)
                .Select(d => new SlotRow
                {
                    Line = $"slot {d.DeviceIndex}  {d.DisplayName}  ({(d.LinkUp ? "online" : "offline")})"
                         + (d.LastKnownBattery is { } b && b.Percent.HasValue ? $"  · {b.Percent}%" : "")
                         + (d.Serial is not null ? $"  · {d.Serial}" : ""),
                })
                .ToList();

            _rows.Add(new ReceiverRow
            {
                Serial = receiver.Info.Serial,
                Header = receiver.Info.ProductString,
                SubLine = $"{(string.IsNullOrEmpty(receiver.Info.Serial) ? "no serial" : receiver.Info.Serial)} · path: {Shorten(receiver.Info.Path)}",
                Slots = slots,
            });
        }

        BuildTopology(receivers);
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

    /// <summary>
    /// Surface hook still wired to the App layer's tray label refresh,
    /// even though there's no in-app host-rename UI anymore. Left as a
    /// stable callback so future refresh sources (CLI changes, settings
    /// file edits) can fire it without touching App.axaml.cs.
    /// </summary>
    public Action? HostNamesChanged { get; set; }

    public sealed class ReceiverRow
    {
        public string Serial { get; set; } = "";
        public string Header { get; set; } = "";
        public string SubLine { get; set; } = "";
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

    // ====================================================================
    // Status tab — labeled rows, local receivers + up to 2 network peers
    // ====================================================================

    private DispatcherTimer? _diagTimer;

    private readonly ObservableCollection<LocalReceiverRow> _localReceivers = new();
    private readonly ObservableCollection<PeerRow> _peers = new();

    private void StartDiagnosticsTimer()
    {
        // Wire row sources on first show.
        var localList = this.FindControl<ItemsControl>("LocalReceiversList");
        if (localList is not null) localList.ItemsSource = _localReceivers;
        var peersList = this.FindControl<ItemsControl>("PeersList");
        if (peersList is not null) peersList.ItemsSource = _peers;

        _diagTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _diagTimer.Tick += (_, _) => RefreshStatusTab();
        _diagTimer.Start();
        _disposables.Add(System.Reactive.Disposables.Disposable.Create(() =>
        {
            _diagTimer.Stop();
            _diagTimer = null;
        }));
        RefreshStatusTab();
    }

    private void RefreshStatusTab()
    {
        try
        {
            RefreshLocalSection();
            RefreshPeersSection();
        }
        catch
        {
            // best-effort: never let a refresh exception kill the timer
        }
    }

    private void RefreshLocalSection()
    {
        _localReceivers.Clear();
        var empty = this.FindControl<TextBlock>("LocalEmptyLine");
        if (_manager is null || _manager.Receivers.Count == 0)
        {
            if (empty is not null) empty.IsVisible = true;
            return;
        }
        if (empty is not null) empty.IsVisible = false;

        foreach (var r in _manager.Receivers.Items)
        {
            var details = r.LastKnownDetails;
            var fw = details?.FirmwareVersionString;
            var serial = !string.IsNullOrEmpty(r.Info.Serial) ? r.Info.Serial
                        : details?.Serial ?? "(no serial)";

            var devices = new List<LocalDeviceRow>();
            foreach (var d in r.Devices.Items.OrderBy(d => (int)d.DeviceIndex))
            {
                var link = d.LinkUp ? "● online" : "○ offline";
                var battery = d.LastKnownBattery is { } b
                    ? (b.Percent.HasValue
                        ? $"{b.Percent}%{(b.Charging == true ? " (charging)" : "")}"
                        : "?")
                    : null;

                string currentHostText = "";
                if (d.LastKnownCurrentHost is byte cur)
                {
                    var receiverName = d.HostBindings.TryGetValue(cur, out var bind)
                                       && !string.IsNullOrEmpty(bind.ReceiverName)
                        ? bind.ReceiverName!
                        : "(unnamed)";
                    currentHostText = $"H{cur + 1} → {receiverName}";
                }

                devices.Add(new LocalDeviceRow
                {
                    Header = $"Slot {d.DeviceIndex} · {d.DisplayName} · wpid 0x{d.Wpid:X4}",
                    SubLine = string.Join("   ", new[]
                    {
                        link,
                        string.IsNullOrEmpty(currentHostText) ? null : $"current {currentHostText}",
                        battery is null ? null : $"battery {battery}",
                    }.Where(s => !string.IsNullOrEmpty(s))!),
                });
            }

            _localReceivers.Add(new LocalReceiverRow
            {
                Header = r.Info.ProductString,
                Firmware = fw is null ? "(fw unknown)" : $"fw {fw}",
                SerialLine = $"serial {serial}",
                Devices = devices,
            });
        }
    }

    private void RefreshPeersSection()
    {
        _peers.Clear();
        var stateLine = this.FindControl<TextBlock>("PeersStateLine");
        var overflowLine = this.FindControl<TextBlock>("PeersOverflowLine");
        if (overflowLine is not null) overflowLine.IsVisible = false;

        if (_settings is null || !_settings.Topology.Enabled)
        {
            if (stateLine is not null) stateLine.Text = "Cross-machine sync is off. Enable it in the Network tab to see peers.";
            return;
        }

        var peers = PeerStatsProvider?.Invoke()?.ToList() ?? new List<LogiPlusSwitcher.Core.Topology.PeerStats>();
        var anns = PeerAnnouncementsProvider?.Invoke()?.ToDictionary(a => a.MachineId, a => a)
                   ?? new Dictionary<string, LogiPlusSwitcher.Core.Topology.ReceiverAnnouncement>();

        if (peers.Count == 0 && anns.Count == 0)
        {
            if (stateLine is not null) stateLine.Text = "Cross-machine sync is on. No peers discovered yet.";
            return;
        }

        if (stateLine is not null) stateLine.Text = $"Cross-machine sync is on. {peers.Count} peer{(peers.Count == 1 ? "" : "s")} reachable.";

        // Logi devices support 3 hosts max → a sync group has at most 3
        // machines (this one + up to 2 peers). Show the two most-recently-
        // seen; surface the rest as an overflow line.
        var ordered = peers
            .OrderByDescending(p => p.LastSeenUtc)
            .ToList();
        const int MaxPeersShown = 2;
        foreach (var peer in ordered.Take(MaxPeersShown))
        {
            anns.TryGetValue(peer.MachineId, out var ann);
            _peers.Add(BuildPeerRow(peer, ann));
        }
        var extra = ordered.Count - MaxPeersShown;
        if (extra > 0 && overflowLine is not null)
        {
            overflowLine.IsVisible = true;
            overflowLine.Text = $"+ {extra} more peer{(extra == 1 ? "" : "s")} not displayed (sync group caps at 3 machines)";
        }
    }

    private static PeerRow BuildPeerRow(
        LogiPlusSwitcher.Core.Topology.PeerStats peer,
        LogiPlusSwitcher.Core.Topology.ReceiverAnnouncement? ann)
    {
        var hostname = string.IsNullOrEmpty(peer.Hostname) ? "(unknown host)" : peer.Hostname;
        var shortId = peer.MachineId.Length > 8 ? peer.MachineId[..8] : peer.MachineId;

        var sinceMs = (DateTime.UtcNow - peer.LastSeenUtc).TotalMilliseconds;
        var sinceStr = peer.LastSeenUtc == default
            ? "never"
            : sinceMs < 1500 ? $"{sinceMs:F0}ms ago"
                             : sinceMs < 60_000 ? $"{sinceMs / 1000:F1}s ago"
                                                : $"{sinceMs / 60_000:F1}m ago";
        var alive = sinceMs < 10_000 ? "online" : "silent";

        var receiverSummary = ann is null
            ? "(no announcement payload yet)"
            : string.Join(" · ", ann.Receivers.Select(r =>
            {
                var ser = string.IsNullOrEmpty(r.Serial) ? "no-serial" : r.Serial;
                return $"rcvr {ser}";
            }));

        var deviceLines = new List<PeerDeviceRow>();
        if (ann is not null)
        {
            foreach (var rentry in ann.Receivers)
            {
                if (rentry.OnlineDevices.Count == 0) continue;
                foreach (var od in rentry.OnlineDevices)
                {
                    var name = string.IsNullOrEmpty(od.Name) ? "(no name)" : od.Name;
                    deviceLines.Add(new PeerDeviceRow
                    {
                        Line = $"  ● slot {od.Slot} · wpid 0x{od.WpidHex} · {name}",
                    });
                }
            }
        }
        if (deviceLines.Count == 0)
            deviceLines.Add(new PeerDeviceRow { Line = "  (no devices online on this peer)" });

        return new PeerRow
        {
            Header = $"{hostname}  [{alive}]",
            LastSeen = $"last seen {sinceStr}",
            MetaLine = $"machine {shortId}   {receiverSummary}   recv {peer.UniqueReceived}",
            Devices = deviceLines,
        };
    }

    public sealed class LocalReceiverRow
    {
        public string Header { get; set; } = "";
        public string Firmware { get; set; } = "";
        public string SerialLine { get; set; } = "";
        public List<LocalDeviceRow> Devices { get; set; } = new();
    }

    public sealed class LocalDeviceRow
    {
        public string Header { get; set; } = "";
        public string SubLine { get; set; } = "";
    }

    public sealed class PeerRow
    {
        public string Header { get; set; } = "";
        public string LastSeen { get; set; } = "";
        public string MetaLine { get; set; } = "";
        public List<PeerDeviceRow> Devices { get; set; } = new();
    }

    public sealed class PeerDeviceRow
    {
        public string Line { get; set; } = "";
    }
}
