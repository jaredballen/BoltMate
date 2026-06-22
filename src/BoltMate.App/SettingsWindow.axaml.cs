using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using BoltMate.App.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using BoltMate.Core;
using BoltMate.Core.Bolt;
using BoltMate.Core.Permissions;
using BoltMate.Core.Topology;

namespace BoltMate.App;

/// <summary>
/// Settings UX: Status / About / License (side tabs). Status combines online
/// devices, local-network permission, and any LAN peers. About holds the
/// app version, update check, autostart, telemetry, and log folder hooks.
/// License is a placeholder.
/// </summary>
public partial class SettingsWindow : Window
{
    public const string TabStatus = "status";
    public const string TabAbout = "about";
    public const string TabLicense = "license";

    private readonly ReceiverManager? _manager;
    private readonly AppSettings? _settings;
    private IPermissionsService? _permissions;
    private readonly CompositeDisposable _disposables = new();
    private UpdateService? _updates;
    private DispatcherTimer? _statusTimer;

    private readonly ObservableCollection<LocalDeviceRow> _localDevices = new();
    private readonly ObservableCollection<PeerRow> _peers = new();

    public SettingsWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _disposables.Dispose();

        // Pre-warm pattern: the App constructs this window once at startup
        // and keeps it alive across open/close cycles so the second-and-later
        // opens are instant. The user clicking the OS close button must NOT
        // actually destroy the window — cancel the close and hide instead.
        // Real disposal happens when the App tears the window down on shutdown
        // (via Hibernate() below), at which point we let Close go through.
        Closing += (_, e) =>
        {
            if (_hibernating) return; // App is tearing us down: allow close.
            e.Cancel = true;
            Hide();
            OnHidden?.Invoke();
        };

        // Wire NavigationView selection to page-swap. Default to Status page.
        var nav = this.FindControl<FANavigationView>("MainNav");
        if (nav is not null)
        {
            nav.SelectionChanged += OnNavSelectionChanged;
            var statusItem = this.FindControl<FANavigationViewItem>("StatusNavItem");
            if (statusItem is not null) nav.SelectedItem = statusItem;
        }
    }

    private bool _hibernating;

    /// <summary>
    /// Tells the App layer that the user dismissed the window (Closing was
    /// intercepted; the window is now hidden but still alive). The App flips
    /// the macOS Dock icon back to accessory mode here.
    /// </summary>
    public Action? OnHidden { get; set; }

    /// <summary>
    /// Permanently closes the window. Call this from the App shutdown path
    /// after the pre-warmed window is no longer needed.
    /// </summary>
    public void Hibernate()
    {
        _hibernating = true;
        Close();
    }

    private readonly SerialDisposable _udpHealthSub = new();
    private readonly SerialDisposable _syncHealthSub = new();

    public SettingsWindow(
        ReceiverManager manager,
        AppSettings settings,
        IPermissionsService permissions,
        IObservable<TransportHealth>? udpHealth = null,
        IObservable<TransportHealth>? syncHealth = null) : this()
    {
        _manager = manager;
        _settings = settings;
        _permissions = permissions;

        _disposables.Add(_udpHealthSub);
        _disposables.Add(_syncHealthSub);

        var localList = this.FindControl<ItemsControl>("LocalDevicesList");
        if (localList is not null) localList.ItemsSource = _localDevices;
        var peersList = this.FindControl<ItemsControl>("PeersList");
        if (peersList is not null) peersList.ItemsSource = _peers;

        _updates = new UpdateService(settings, NullLogger<UpdateService>.Instance);

        RefreshAbout();
        RefreshLaunchAtLogin();
        RefreshTelemetryToggle();
        StartStatusTimer();

        // Live-bind the Login Items checkbox to the service. User toggling
        // BoltMate off in System Settings → General → Login Items propagates
        // here within the service's polling interval.
        _disposables.Add(_permissions.Autostart.IsGrantedChanged
            .Subscribe(_ => Dispatcher.UIThread.Post(RefreshLaunchAtLogin)));

        BindHealth(udpHealth, syncHealth);
    }

    public void BindHealth(IObservable<TransportHealth>? udpHealth, IObservable<TransportHealth>? syncHealth)
    {
        if (udpHealth is not null)
        {
            _udpHealthSub.Disposable = udpHealth.Subscribe(h => Dispatcher.UIThread.Post(() => UpdateUdpHealthUi(h)));
        }
        else
        {
            _udpHealthSub.Disposable = Disposable.Empty;
            Dispatcher.UIThread.Post(() => UpdateUdpHealthUi(TransportHealth.Unknown("239.255.41.42:41420", "cross-machine sync disabled")));
        }

        if (syncHealth is not null)
        {
            _syncHealthSub.Disposable = syncHealth.Subscribe(h => Dispatcher.UIThread.Post(() => UpdateSyncHealthUi(h)));
        }
        else
        {
            _syncHealthSub.Disposable = Disposable.Empty;
            Dispatcher.UIThread.Post(() => UpdateSyncHealthUi(TransportHealth.Unknown("Bonjour / mDNS + TCP sync", "cross-machine sync disabled")));
        }
    }

    private void UpdateUdpHealthUi(TransportHealth health)
    {
        var indicator = this.FindControl<TextBlock>("UdpStatusIndicator");
        var endpoint = this.FindControl<TextBlock>("UdpEndpoint");
        var detail = this.FindControl<TextBlock>("UdpDetail");

        if (indicator is not null)
        {
            indicator.Foreground = health.State switch
            {
                TransportState.Healthy => Avalonia.Media.Brush.Parse("#10B981"),
                TransportState.Blocked => Avalonia.Media.Brush.Parse("#EF4444"),
                _ => Avalonia.Media.Brush.Parse("#9CA3AF")
            };
        }

        if (endpoint is not null)
        {
            endpoint.Text = health.Endpoint;
        }

        if (detail is not null)
        {
            var stateStr = health.State switch
            {
                TransportState.Healthy => "Healthy",
                TransportState.Blocked => "Blocked",
                _ => "Unknown"
            };
            detail.Text = $"{stateStr} — {health.DetailMessage}";
        }
    }

    private void UpdateSyncHealthUi(TransportHealth health)
    {
        var indicator = this.FindControl<TextBlock>("SyncStatusIndicator");
        var endpoint = this.FindControl<TextBlock>("SyncEndpoint");
        var detail = this.FindControl<TextBlock>("SyncDetail");

        if (indicator is not null)
        {
            indicator.Foreground = health.State switch
            {
                TransportState.Healthy => Avalonia.Media.Brush.Parse("#10B981"),
                TransportState.Blocked => Avalonia.Media.Brush.Parse("#EF4444"),
                _ => Avalonia.Media.Brush.Parse("#9CA3AF")
            };
        }

        if (endpoint is not null)
        {
            endpoint.Text = health.Endpoint;
        }

        if (detail is not null)
        {
            var stateStr = health.State switch
            {
                TransportState.Healthy => "Healthy",
                TransportState.Blocked => "Blocked",
                _ => "Unknown"
            };
            detail.Text = $"{stateStr} — {health.DetailMessage}";
        }
    }

    /// <summary>
    /// Swap visible page when the user clicks a different NavigationViewItem.
    /// </summary>
    private void OnNavSelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
    {
        if (e.SelectedItem is not FANavigationViewItem nvi) return;
        var tag = nvi.Tag as string;
        ShowPage(tag);
    }

    private void ShowPage(string? tag)
    {
        var statusPage = this.FindControl<Control>("StatusPage");
        var aboutPage = this.FindControl<Control>("AboutPage");
        var licensePage = this.FindControl<Control>("LicensePage");
        if (statusPage is not null) statusPage.IsVisible = tag == TabStatus;
        if (aboutPage is not null) aboutPage.IsVisible = tag == TabAbout;
        if (licensePage is not null) licensePage.IsVisible = tag == TabLicense;
    }

    /// <summary>Accessor returning the latest UDP announcement from each known peer.</summary>
    public Func<IEnumerable<BoltMate.Core.Topology.ReceiverAnnouncement>>? PeerAnnouncementsProvider { get; set; }

    /// <summary>Accessor returning per-peer stats from <see cref="BoltMate.Core.Topology.UdpTopologyService.PeerSnapshot"/>.</summary>
    public Func<IEnumerable<BoltMate.Core.Topology.PeerStats>>? PeerStatsProvider { get; set; }

    /// <summary>Accessor returning send-side (attempts, errors) for diagnostics.</summary>
    public Func<(long Attempts, long Errors)>? SendStatsProvider { get; set; }

    /// <summary>Surface hook still wired to the App layer; no-op today.</summary>
    public Action? HostNamesChanged { get; set; }

    /// <summary>Hook for the App layer when network/topology toggles change. Unused after scope cut.</summary>
    public Action? TopologyChanged { get; set; }

    /// <summary>
    /// Selects which page is active. Called by the tray menu so that clicking
    /// a tray entry lands the user on the matching pane.
    /// </summary>
    public void OpenTo(string tab)
    {
        var nav = this.FindControl<FANavigationView>("MainNav");
        if (nav is null) return;
        FANavigationViewItem? target = tab switch
        {
            TabAbout => this.FindControl<FANavigationViewItem>("AboutNavItem"),
            TabLicense => this.FindControl<FANavigationViewItem>("LicenseNavItem"),
            _ => this.FindControl<FANavigationViewItem>("StatusNavItem"),
        };
        if (target is not null) nav.SelectedItem = target;
        // SelectionChanged fires the page swap; in case the same item was
        // already selected, force the page state to match.
        ShowPage(tab);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // ====================================================================
    // About tab
    // ====================================================================

    private bool _suppressTelemetryEvent;
    private bool _suppressLaunchAtLoginEvent;

    private void RefreshAbout()
    {
        if (_updates is null) return;
        var v = this.FindControl<TextBlock>("AboutVersionLine");
        if (v is not null) v.Text = $"Version {_updates.CurrentVersion}";
        var lc = this.FindControl<TextBlock>("LastCheckedLine");
        if (lc is not null)
            lc.Text = _updates.LastCheckUtc?.ToLocalTime().ToString("g") ?? "never";
        var logs = this.FindControl<TextBlock>("LogsPathLine");
        if (logs is not null) logs.Text = $"Logs: {AppPaths.LogsDirectory}";
    }

    private void RefreshTelemetryToggle()
    {
        if (_settings is null) return;
        var t = this.FindControl<CheckBox>("TelemetryToggle");
        if (t is null) return;
        _suppressTelemetryEvent = true;
        t.IsChecked = _settings.TelemetryEnabled;
        _suppressTelemetryEvent = false;
    }

    private void OnTelemetryChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_suppressTelemetryEvent || _settings is null) return;
        if (sender is not CheckBox cb) return;
        _settings.TelemetryEnabled = cb.IsChecked == true;
        _settings.Save();
    }

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

        var granted = _permissions?.Autostart.IsGranted ?? AppAutostart.IsLoaded();
        _suppressLaunchAtLoginEvent = true;
        toggle.IsChecked = granted;
        toggle.IsEnabled = true;
        _suppressLaunchAtLoginEvent = false;
        if (detail is not null)
            detail.Text = granted
                ? "Registered. BoltMate will start automatically when you log in."
                : "Off. Launch manually from Applications / Start Menu.";
    }

    private async void OnLaunchAtLoginChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_suppressLaunchAtLoginEvent) return;
        if (sender is not CheckBox cb) return;
        if (_permissions is null) return;

        var detail = this.FindControl<TextBlock>("LaunchAtLoginDetail");
        var want = cb.IsChecked == true;
        cb.IsEnabled = false;
        try
        {
            var ok = want
                ? await _permissions.Autostart.GrantAsync()
                : await _permissions.Autostart.RevokeAsync();
            if (!ok)
            {
                _suppressLaunchAtLoginEvent = true;
                cb.IsChecked = !want;
                _suppressLaunchAtLoginEvent = false;
                if (detail is not null)
                    detail.Text = $"Could not {(want ? "enable" : "disable")} launch-at-login.";
            }
        }
        finally
        {
            cb.IsEnabled = true;
        }
    }

    private void OnOpenLogsFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        RevealInFileManager(AppPaths.LogsDirectory);

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
            // best-effort
        }
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
            RefreshAbout();
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

    // ====================================================================
    // Status tab — 1Hz refresh of devices + network state + peers
    // ====================================================================

    private void StartStatusTimer()
    {
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        _disposables.Add(Disposable.Create(() =>
        {
            _statusTimer.Stop();
            _statusTimer = null;
        }));
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        try
        {
            RefreshLocalSection();
            RefreshNetworkSection();
            RefreshPeersSection();
        }
        catch
        {
            // never let a refresh exception kill the timer
        }
    }

    private void RefreshLocalSection()
    {
        _localDevices.Clear();
        var empty = this.FindControl<TextBlock>("LocalEmptyLine");

        var any = false;
        if (_manager is not null)
        {
            foreach (var r in _manager.Receivers.Items)
            {
                foreach (var d in r.Devices.Items.OrderBy(d => (int)d.DeviceIndex))
                {
                    if (!d.LinkUp) continue;
                    any = true;

                    var battery = d.LastKnownBattery is { } b
                        ? (b.Percent.HasValue
                            ? $"{b.Percent}%{(b.Charging == true ? " (charging)" : "")}"
                            : "?")
                        : null;

                    string currentHostText = "";
                    if (d.LastKnownCurrentHost is byte cur)
                    {
                        var hostName = d.HostBindings.TryGetValue(cur, out var bind)
                                       && !string.IsNullOrEmpty(bind.ReceiverName)
                            ? bind.ReceiverName!
                            : "(unnamed)";
                        currentHostText = $"H{cur + 1} → {hostName}";
                    }

                    var subParts = new[]
                    {
                        string.IsNullOrEmpty(currentHostText) ? null : $"current {currentHostText}",
                        battery is null ? null : $"battery {battery}",
                    }.Where(s => !string.IsNullOrEmpty(s)).ToArray();

                    _localDevices.Add(new LocalDeviceRow
                    {
                        Header = $"{d.DisplayName} · wpid 0x{d.Wpid:X4}",
                        SubLine = string.Join("   ", subParts!),
                    });
                }
            }
        }

        if (empty is not null) empty.IsVisible = !any;
    }

    private void RefreshNetworkSection()
    {
        var line = this.FindControl<TextBlock>("NetworkPermissionLine");
        var btn = this.FindControl<Button>("OpenNetworkSettingsButton");
        var res = NetworkPermission.Check();
        if (line is not null) line.Text = res.Detail;
        if (btn is not null)
        {
            btn.IsVisible = res.Status == NetworkPermission.Status.Denied;
            btn.Content = OperatingSystem.IsWindows() ? "Open Network Settings" : "Open Privacy Settings";
        }
    }

    private void OnOpenNetworkSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        NetworkPermission.OpenSystemSettings();
        NetworkPermission.Invalidate();
    }

    private void RefreshPeersSection()
    {
        _peers.Clear();
        var stateLine = this.FindControl<TextBlock>("PeersStateLine");
        var overflowLine = this.FindControl<TextBlock>("PeersOverflowLine");
        if (overflowLine is not null) overflowLine.IsVisible = false;

        var peers = PeerStatsProvider?.Invoke()?.ToList() ?? new List<BoltMate.Core.Topology.PeerStats>();
        var anns = PeerAnnouncementsProvider?.Invoke()?.ToDictionary(a => a.MachineId, a => a)
                   ?? new Dictionary<string, BoltMate.Core.Topology.ReceiverAnnouncement>();

        if (peers.Count == 0 && anns.Count == 0)
        {
            if (stateLine is not null) stateLine.Text = "No peers discovered yet.";
            return;
        }

        if (stateLine is not null)
            stateLine.Text = $"{peers.Count} peer{(peers.Count == 1 ? "" : "s")} reachable.";

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
            overflowLine.Text = $"+ {extra} more peer{(extra == 1 ? "" : "s")} not displayed";
        }
    }

    private static PeerRow BuildPeerRow(
        BoltMate.Core.Topology.PeerStats peer,
        BoltMate.Core.Topology.ReceiverAnnouncement? ann)
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

        var deviceLines = new List<PeerDeviceRow>();
        if (ann is not null)
        {
            foreach (var rentry in ann.Receivers)
            {
                if (rentry.Devices.Count == 0) continue;
                foreach (var od in rentry.Devices)
                {
                    if (!od.LinkUp) continue;
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
            MetaLine = $"machine {shortId}   recv {peer.UniqueReceived}",
            Devices = deviceLines,
        };
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
