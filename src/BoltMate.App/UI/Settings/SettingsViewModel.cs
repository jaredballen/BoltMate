using BoltMate.Core.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Threading;
using BoltMate.App.Services;
using BoltMate.Core;
using BoltMate.Core.Bolt;
using BoltMate.Core.Permissions;
using BoltMate.Core.Topology;
using ReactiveUI;

namespace BoltMate.App.UI;

/// <summary>
/// View-model for <see cref="SettingsWindow"/>. Owns all state + behaviour
/// for the Status and About tabs; window code-behind is reduced to
/// Avalonia-specific plumbing (Closing intercept, NavigationView page swap,
/// XAML init).
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly IReceiverManager _manager;
    private readonly AppSettings _settings;
    private readonly IPermissionsService _permissions;
    private readonly UpdateService _updates;
    private readonly BoltMate.App.Core.Notifications.INotificationService? _notifications;
    private readonly IObservable<TransportHealth>? _udpHealth;
    private readonly IObservable<TransportHealth>? _syncHealth;
    private readonly Func<IEnumerable<ReceiverAnnouncement>>? _peerAnnouncementsProvider;
    private readonly Func<IEnumerable<PeerStats>>? _peerStatsProvider;

    /// <summary>Per-window subscriptions disposed on Window.Closing.</summary>
    public CompositeDisposable Activation { get; } = new();

    // Exposed so the window's BindHealth() can re-construct the VM with
    // fresh transport-health observables when the user toggles topology on
    // or off without restarting the app.
    internal IReceiverManager Manager => _manager;
    internal AppSettings Settings => _settings;
    internal IPermissionsService Permissions => _permissions;
    internal UpdateService Updates => _updates;
    internal BoltMate.App.Core.Notifications.INotificationService? Notifications => _notifications;

    // ---- Status tab ----------------------------------------------------

    public ObservableCollection<LocalDeviceRow> LocalDevices { get; } = new();

    public ObservableCollection<PeerRow> Peers { get; } = new();

    private bool _localEmpty;
    public bool LocalEmpty
    {
        get => _localEmpty;
        set => this.RaiseAndSetIfChanged(ref _localEmpty, value);
    }

    private string _networkPermissionDetail = "";
    public string NetworkPermissionDetail
    {
        get => _networkPermissionDetail;
        set => this.RaiseAndSetIfChanged(ref _networkPermissionDetail, value);
    }

    private bool _showOpenNetworkSettings;
    public bool ShowOpenNetworkSettings
    {
        get => _showOpenNetworkSettings;
        set => this.RaiseAndSetIfChanged(ref _showOpenNetworkSettings, value);
    }

    public string OpenNetworkSettingsLabel { get; } =
        OperatingSystem.IsWindows() ? "Open Network Settings" : "Open Privacy Settings";

    private string _udpStateLabel = "Unknown";
    public string UdpStateLabel
    {
        get => _udpStateLabel;
        set => this.RaiseAndSetIfChanged(ref _udpStateLabel, value);
    }

    private string _udpEndpoint = "";
    public string UdpEndpoint
    {
        get => _udpEndpoint;
        set => this.RaiseAndSetIfChanged(ref _udpEndpoint, value);
    }

    private string _udpDetail = "";
    public string UdpDetail
    {
        get => _udpDetail;
        set => this.RaiseAndSetIfChanged(ref _udpDetail, value);
    }

    private string _udpIndicatorColor = "#9CA3AF";
    public string UdpIndicatorColor
    {
        get => _udpIndicatorColor;
        set => this.RaiseAndSetIfChanged(ref _udpIndicatorColor, value);
    }

    private string _syncStateLabel = "Unknown";
    public string SyncStateLabel
    {
        get => _syncStateLabel;
        set => this.RaiseAndSetIfChanged(ref _syncStateLabel, value);
    }

    private string _syncEndpoint = "";
    public string SyncEndpoint
    {
        get => _syncEndpoint;
        set => this.RaiseAndSetIfChanged(ref _syncEndpoint, value);
    }

    private string _syncDetail = "";
    public string SyncDetail
    {
        get => _syncDetail;
        set => this.RaiseAndSetIfChanged(ref _syncDetail, value);
    }

    private string _syncIndicatorColor = "#9CA3AF";
    public string SyncIndicatorColor
    {
        get => _syncIndicatorColor;
        set => this.RaiseAndSetIfChanged(ref _syncIndicatorColor, value);
    }

    private string _peersStateLine = "No peers discovered yet.";
    public string PeersStateLine
    {
        get => _peersStateLine;
        set => this.RaiseAndSetIfChanged(ref _peersStateLine, value);
    }

    private bool _showPeersOverflow;
    public bool ShowPeersOverflow
    {
        get => _showPeersOverflow;
        set => this.RaiseAndSetIfChanged(ref _showPeersOverflow, value);
    }

    private string _peersOverflowLine = "";
    public string PeersOverflowLine
    {
        get => _peersOverflowLine;
        set => this.RaiseAndSetIfChanged(ref _peersOverflowLine, value);
    }

    // ---- About tab -----------------------------------------------------

    private string _versionLine = "";
    public string VersionLine
    {
        get => _versionLine;
        set => this.RaiseAndSetIfChanged(ref _versionLine, value);
    }

    private string _lastCheckedLine = "never";
    public string LastCheckedLine
    {
        get => _lastCheckedLine;
        set => this.RaiseAndSetIfChanged(ref _lastCheckedLine, value);
    }

    private string _logsPathLine = "";
    public string LogsPathLine
    {
        get => _logsPathLine;
        set => this.RaiseAndSetIfChanged(ref _logsPathLine, value);
    }

    private bool _notificationsEnabled;
    public bool NotificationsEnabled
    {
        get => _notificationsEnabled;
        set => this.RaiseAndSetIfChanged(ref _notificationsEnabled, value);
    }

    private string _notificationsStatusLine = "";
    public string NotificationsStatusLine
    {
        get => _notificationsStatusLine;
        set => this.RaiseAndSetIfChanged(ref _notificationsStatusLine, value);
    }

    // Status-pill state, mirroring the Privacy / Local Network style
    // used elsewhere in Settings. Three derived properties bind directly
    // into XAML so the pill changes colour + label as OS state flips.
    private string _notificationsPillText = "Disabled";
    public string NotificationsPillText
    {
        get => _notificationsPillText;
        set => this.RaiseAndSetIfChanged(ref _notificationsPillText, value);
    }

    private string _notificationsPillBackground = "#1A3F3F46";
    public string NotificationsPillBackground
    {
        get => _notificationsPillBackground;
        set => this.RaiseAndSetIfChanged(ref _notificationsPillBackground, value);
    }

    private string _notificationsPillForeground = "#9CA3AF";
    public string NotificationsPillForeground
    {
        get => _notificationsPillForeground;
        set => this.RaiseAndSetIfChanged(ref _notificationsPillForeground, value);
    }

    private string _notificationsPillDot = "#9CA3AF";
    public string NotificationsPillDot
    {
        get => _notificationsPillDot;
        set => this.RaiseAndSetIfChanged(ref _notificationsPillDot, value);
    }

    private bool _launchAtLoginEnabled;
    public bool LaunchAtLoginEnabled
    {
        get => _launchAtLoginEnabled;
        set => this.RaiseAndSetIfChanged(ref _launchAtLoginEnabled, value);
    }

    private bool _launchAtLoginChecked;
    public bool LaunchAtLoginChecked
    {
        get => _launchAtLoginChecked;
        set => this.RaiseAndSetIfChanged(ref _launchAtLoginChecked, value);
    }

    private string _launchAtLoginDetail = "";
    public string LaunchAtLoginDetail
    {
        get => _launchAtLoginDetail;
        set => this.RaiseAndSetIfChanged(ref _launchAtLoginDetail, value);
    }

    private bool _telemetryEnabled;
    public bool TelemetryEnabled
    {
        get => _telemetryEnabled;
        set => this.RaiseAndSetIfChanged(ref _telemetryEnabled, value);
    }

    private string _updateStatusLine = "";
    public string UpdateStatusLine
    {
        get => _updateStatusLine;
        set => this.RaiseAndSetIfChanged(ref _updateStatusLine, value);
    }

    private bool _canCheckForUpdates = true;
    public bool CanCheckForUpdates
    {
        get => _canCheckForUpdates;
        set => this.RaiseAndSetIfChanged(ref _canCheckForUpdates, value);
    }

    // ---- Commands ------------------------------------------------------

    public ReactiveCommand<Unit, Unit> OpenNetworkSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> CheckForUpdatesCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenLogsFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenNotificationSettingsCommand { get; }

    public SettingsViewModel(
        IReceiverManager manager,
        AppSettings settings,
        IPermissionsService permissions,
        UpdateService updates,
        BoltMate.App.Core.Notifications.INotificationService? notifications = null,
        IObservable<TransportHealth>? udpHealth = null,
        IObservable<TransportHealth>? syncHealth = null,
        Func<IEnumerable<ReceiverAnnouncement>>? peerAnnouncementsProvider = null,
        Func<IEnumerable<PeerStats>>? peerStatsProvider = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(updates);

        _manager = manager;
        _settings = settings;
        _permissions = permissions;
        _updates = updates;
        _notifications = notifications;
        _udpHealth = udpHealth;
        _syncHealth = syncHealth;
        _peerAnnouncementsProvider = peerAnnouncementsProvider;
        _peerStatsProvider = peerStatsProvider;

        RefreshAboutStatic();
        RefreshTelemetryToggle();
        RefreshLaunchAtLogin();

        // Two-way bind sinks for toggles. Skip(1) suppresses the initial
        // value we just wrote during the Refresh* calls above.
        this.WhenAnyValue(x => x.TelemetryEnabled)
            .Skip(1)
            .Subscribe(OnTelemetryChanged);

        this.WhenAnyValue(x => x.LaunchAtLoginChecked)
            .Skip(1)
            .Subscribe(want => _ = OnLaunchAtLoginChangedAsync(want));

        OpenNetworkSettingsCommand = ReactiveCommand.Create(() =>
        {
            NetworkPermission.OpenSystemSettings();
            NetworkPermission.Invalidate();
        });

        CheckForUpdatesCommand = ReactiveCommand.CreateFromTask(CheckForUpdatesAsync,
            this.WhenAnyValue(x => x.CanCheckForUpdates));

        OpenLogsFolderCommand = ReactiveCommand.Create(() => RevealInFileManager(AppPaths.LogsDirectory));

        OpenNotificationSettingsCommand = ReactiveCommand.Create(() =>
        {
            _notifications?.OpenOsSettings();
        });
    }

    /// <summary>
    /// Called by <see cref="SettingsWindow"/> on window activation. Wires
    /// the 1 Hz status refresh, transport-health subscriptions, and the
    /// autostart-state observer. All subscriptions land in
    /// <see cref="Activation"/> and are disposed on deactivation.
    /// </summary>
    public void WireActivation()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshStatus();
        timer.Start();
        Activation.Add(Disposable.Create(timer.Stop));
        RefreshStatus();

        if (_udpHealth is not null)
            Activation.Add(_udpHealth.Subscribe(h => Dispatcher.UIThread.Post(() => UpdateUdpHealth(h))));
        else
            Dispatcher.UIThread.Post(() => UpdateUdpHealth(TransportHealth.Unknown("239.255.41.42:41420", "cross-machine sync disabled")));

        if (_syncHealth is not null)
            Activation.Add(_syncHealth.Subscribe(h => Dispatcher.UIThread.Post(() => UpdateSyncHealth(h))));
        else
            Dispatcher.UIThread.Post(() => UpdateSyncHealth(TransportHealth.Unknown("Bonjour / mDNS + TCP sync", "cross-machine sync disabled")));

        Activation.Add(_permissions.Autostart.IsGrantedChanged
            .Subscribe(_ => Dispatcher.UIThread.Post(RefreshLaunchAtLogin)));

        // Toggle visual + status line both reflect OS state via the
        // existing 1Hz permission probe. Single source of truth.
        Activation.Add(_permissions.Notifications.IsGrantedChanged
            .Subscribe(_ => Dispatcher.UIThread.Post(RefreshNotificationsCard)));
        RefreshNotificationsCard();
    }

    private void RefreshNotificationsCard()
    {
        var osGranted = _permissions.Notifications.IsGranted;
        NotificationsEnabled = osGranted;

        if (osGranted)
        {
            NotificationsPillText = "Enabled";
            NotificationsPillBackground = "#1A22C55E";   // green/10
            NotificationsPillForeground = "#22C55E";
            NotificationsPillDot = "#22C55E";
            NotificationsStatusLine = "BoltMate can notify you. To turn alerts off or change banners and sounds, use System Settings.";
        }
        else
        {
            NotificationsPillText = "Disabled";
            NotificationsPillBackground = "#1A9CA3AF";   // grey/10
            NotificationsPillForeground = "#9CA3AF";
            NotificationsPillDot = "#9CA3AF";
            NotificationsStatusLine = "BoltMate can't post notifications right now. Enable in System Settings to get alerts when something needs your attention.";
        }
    }

    /// <summary>Dispose per-activation subscriptions (window hide / close).</summary>
    public void TeardownActivation() => Activation.Dispose();

    // ---- Status refresh ------------------------------------------------

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
        LocalDevices.Clear();
        var any = false;
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

                var currentHostText = "";
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

                LocalDevices.Add(new LocalDeviceRow
                {
                    Header = $"{d.DisplayName} · wpid 0x{d.Wpid:X4}",
                    SubLine = string.Join("   ", subParts!),
                });
            }
        }
        LocalEmpty = !any;
    }

    private void RefreshNetworkSection()
    {
        var res = NetworkPermission.Check();
        NetworkPermissionDetail = res.Detail;
        ShowOpenNetworkSettings = res.Status is NetworkPermission.Status.Denied;
    }

    private void RefreshPeersSection()
    {
        Peers.Clear();
        ShowPeersOverflow = false;

        var peerList = _peerStatsProvider?.Invoke()?.ToList() ?? new List<PeerStats>();
        var anns = _peerAnnouncementsProvider?.Invoke()?.ToDictionary(a => a.MachineId, a => a)
                   ?? new Dictionary<string, ReceiverAnnouncement>();

        if (peerList.Count == 0 && anns.Count == 0)
        {
            PeersStateLine = "No peers discovered yet.";
            return;
        }

        PeersStateLine = $"{peerList.Count} peer{(peerList.Count == 1 ? "" : "s")} reachable.";

        var ordered = peerList.OrderByDescending(p => p.LastSeenUtc).ToList();
        const int MaxPeersShown = 2;
        foreach (var peer in ordered.Take(MaxPeersShown))
        {
            anns.TryGetValue(peer.MachineId, out var ann);
            Peers.Add(BuildPeerRow(peer, ann));
        }
        var extra = ordered.Count - MaxPeersShown;
        if (extra > 0)
        {
            ShowPeersOverflow = true;
            PeersOverflowLine = $"+ {extra} more peer{(extra == 1 ? "" : "s")} not displayed";
        }
    }

    private static PeerRow BuildPeerRow(PeerStats peer, ReceiverAnnouncement? ann)
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

    // ---- Health updates -----------------------------------------------

    private void UpdateUdpHealth(TransportHealth h)
    {
        UdpEndpoint = h.Endpoint;
        var (label, color) = LabelAndColor(h.State);
        UdpStateLabel = label;
        UdpIndicatorColor = color;
        UdpDetail = $"{label} — {h.DetailMessage}";
    }

    private void UpdateSyncHealth(TransportHealth h)
    {
        SyncEndpoint = h.Endpoint;
        var (label, color) = LabelAndColor(h.State);
        SyncStateLabel = label;
        SyncIndicatorColor = color;
        SyncDetail = $"{label} — {h.DetailMessage}";
    }

    private static (string Label, string Color) LabelAndColor(TransportState state) => state switch
    {
        TransportState.Healthy => ("Healthy", "#10B981"),
        TransportState.Blocked => ("Blocked", "#EF4444"),
        _ => ("Unknown", "#9CA3AF"),
    };

    // ---- About tab helpers --------------------------------------------

    private void RefreshAboutStatic()
    {
        VersionLine = $"Version {_updates.CurrentVersion}";
        LastCheckedLine = _updates.LastCheckUtc?.ToLocalTime().ToString("g") ?? "never";
        LogsPathLine = $"Logs: {AppPaths.LogsDirectory}";
    }

    private void RefreshTelemetryToggle()
    {
        TelemetryEnabled = _settings.TelemetryEnabled;
    }

    private void RefreshLaunchAtLogin()
    {
        if (!AppAutostart.CanRegister())
        {
            LaunchAtLoginEnabled = false;
            LaunchAtLoginChecked = false;
            LaunchAtLoginDetail = "Disabled: run from a published build (not 'dotnet run') to enable launch-at-login.";
            return;
        }

        var granted = _permissions.Autostart.IsGranted;
        LaunchAtLoginEnabled = true;
        LaunchAtLoginChecked = granted;
        LaunchAtLoginDetail = granted
            ? "Registered. BoltMate will start automatically when you log in."
            : "Off. Launch manually from Applications / Start Menu.";
    }

    private void OnTelemetryChanged(bool enabled)
    {
        _settings.TelemetryEnabled = enabled;
        _settings.Save();
    }

    private async System.Threading.Tasks.Task OnLaunchAtLoginChangedAsync(bool want)
    {
        LaunchAtLoginEnabled = false;
        try
        {
            var ok = want
                ? await _permissions.Autostart.GrantAsync()
                : await _permissions.Autostart.RevokeAsync();
            if (!ok)
            {
                LaunchAtLoginChecked = !want;
                LaunchAtLoginDetail = $"Could not {(want ? "enable" : "disable")} launch-at-login.";
            }
        }
        finally
        {
            LaunchAtLoginEnabled = true;
        }
    }

    private async System.Threading.Tasks.Task CheckForUpdatesAsync()
    {
        CanCheckForUpdates = false;
        UpdateStatusLine = "Checking…";
        try
        {
            var info = await _updates.CheckAsync();
            UpdateStatusLine = info is null
                ? $"You're up to date on {_updates.CurrentVersion}."
                : $"Update available: {info.Version}. Download: {info.DownloadUrl}";
            RefreshAboutStatic();
        }
        catch (Exception ex)
        {
            UpdateStatusLine = $"Update check failed: {ex.Message}";
        }
        finally
        {
            CanCheckForUpdates = true;
        }
    }

    private static void RevealInFileManager(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", $"\"{path}\"");
            else if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            else
                System.Diagnostics.Process.Start("xdg-open", $"\"{path}\"");
        }
        catch
        {
            // best-effort
        }
    }

    // ---- Data shapes ---------------------------------------------------

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
