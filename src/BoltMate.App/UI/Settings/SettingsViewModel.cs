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

    // ---- Tab selection ------------------------------------------------
    //
    // Three tabs, single visible at a time. The window's XAML binds
    // each tab page's IsVisible to the matching ShowXxxTab flag; nav
    // buttons fire SelectTabCommand with the tab key as parameter.
    //
    // Tab keys are stable strings (also used as App-layer "OpenTo"
    // arguments from the tray menu) so they survive renames of the
    // user-facing labels — "General" was previously "About".

    public const string TabStatus  = "status";
    public const string TabGeneral = "general";
    public const string TabLicense = "license";

    private string _currentTab = TabStatus;
    public string CurrentTab
    {
        get => _currentTab;
        set
        {
            if (_currentTab == value) return;
            this.RaiseAndSetIfChanged(ref _currentTab, value);
            this.RaisePropertyChanged(nameof(ShowStatusTab));
            this.RaisePropertyChanged(nameof(ShowGeneralTab));
            this.RaisePropertyChanged(nameof(ShowLicenseTab));
            this.RaisePropertyChanged(nameof(IsStatusActive));
            this.RaisePropertyChanged(nameof(IsGeneralActive));
            this.RaisePropertyChanged(nameof(IsLicenseActive));
        }
    }

    public bool ShowStatusTab  => CurrentTab == TabStatus;
    public bool ShowGeneralTab => CurrentTab == TabGeneral;
    public bool ShowLicenseTab => CurrentTab == TabLicense;

    // Per-item "is this the active tab" flags drive the nav button
    // background/foreground via DynamicResource bindings in XAML.
    public bool IsStatusActive  => ShowStatusTab;
    public bool IsGeneralActive => ShowGeneralTab;
    public bool IsLicenseActive => ShowLicenseTab;

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

    private bool _peersEmpty = true;
    public bool PeersEmpty
    {
        get => _peersEmpty;
        set => this.RaiseAndSetIfChanged(ref _peersEmpty, value);
    }

    private bool _udpBlocked;
    private bool _syncBlocked;
    private bool _networkBlocked;
    /// <summary>True when ANY transport is in the Blocked state. Drives the
    /// This-Mac card's "Network messages are blocked" error state and
    /// hides the Network + Other-computers cards per design §2a.</summary>
    public bool NetworkBlocked
    {
        get => _networkBlocked;
        private set
        {
            this.RaiseAndSetIfChanged(ref _networkBlocked, value);
            this.RaisePropertyChanged(nameof(ShowNetworkCard));
            this.RaisePropertyChanged(nameof(ShowPeersCard));
            this.RaisePropertyChanged(nameof(ShowLocalDevicesList));
            this.RaisePropertyChanged(nameof(ShowNoDevicesEmpty));
        }
    }

    private bool _noReceiver = true;
    /// <summary>True when no Bolt receiver is attached. Takes priority over
    /// network-blocked + empty-devices states; hides Network + Other-computers
    /// cards because there's nothing to sync.</summary>
    public bool NoReceiver
    {
        get => _noReceiver;
        private set
        {
            this.RaiseAndSetIfChanged(ref _noReceiver, value);
            this.RaisePropertyChanged(nameof(ShowNetworkCard));
            this.RaisePropertyChanged(nameof(ShowPeersCard));
            this.RaisePropertyChanged(nameof(ShowLocalDevicesList));
            this.RaisePropertyChanged(nameof(ShowNoDevicesEmpty));
        }
    }

    /// <summary>Network transports card hides whenever the This-Mac card is
    /// in an error/empty state — no receiver or blocked transports.</summary>
    public bool ShowNetworkCard => !NetworkBlocked && !NoReceiver;

    /// <summary>Other-computers card hides whenever transports are blocked
    /// or no receiver is attached — by protocol we can't know what's reachable.</summary>
    public bool ShowPeersCard => !NetworkBlocked && !NoReceiver;

    /// <summary>The actual list of linked devices is shown only when at
    /// least one is present AND no overriding error state is active.</summary>
    public bool ShowLocalDevicesList => !NetworkBlocked && !NoReceiver && !_localEmpty;

    /// <summary>"No devices linked up" empty state hides when an overriding
    /// error state (NoReceiver, NetworkBlocked) is active.</summary>
    public bool ShowNoDevicesEmpty => !NetworkBlocked && !NoReceiver && _localEmpty;

    private bool _localEmpty;
    public bool LocalEmpty
    {
        get => _localEmpty;
        set
        {
            this.RaiseAndSetIfChanged(ref _localEmpty, value);
            this.RaisePropertyChanged(nameof(ShowLocalDevicesList));
            this.RaisePropertyChanged(nameof(ShowNoDevicesEmpty));
        }
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

    private string _udpIndicatorColor = "#C2C2C8";
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

    private string _syncIndicatorColor = "#C2C2C8";
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

    private string _notificationsPillForeground = "#C2C2C8";
    public string NotificationsPillForeground
    {
        get => _notificationsPillForeground;
        set => this.RaiseAndSetIfChanged(ref _notificationsPillForeground, value);
    }

    private string _notificationsPillDot = "#C2C2C8";
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
        set
        {
            this.RaiseAndSetIfChanged(ref _launchAtLoginChecked, value);
            this.RaisePropertyChanged(nameof(LaunchAtLoginWord));
        }
    }

    /// <summary>Bindable "On"/"Off" caption to the right of the toggle checkbox.</summary>
    public string LaunchAtLoginWord => _launchAtLoginChecked ? "On" : "Off";

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
        set
        {
            this.RaiseAndSetIfChanged(ref _telemetryEnabled, value);
            this.RaisePropertyChanged(nameof(TelemetryWord));
        }
    }

    /// <summary>Bindable "On"/"Off" caption next to the diagnostics toggle.</summary>
    public string TelemetryWord => _telemetryEnabled ? "On" : "Off";

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
    /// <summary>Nav-button-bound tab switcher. Parameter is a tab key constant.</summary>
    public ReactiveCommand<string, Unit> SelectTabCommand { get; }

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

        SelectTabCommand = ReactiveCommand.Create<string>(tab =>
        {
            if (!string.IsNullOrEmpty(tab)) CurrentTab = tab;
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

        // Pill colours track the design tokens for notification status:
        //   Enabled    → green dot #7FBF3F on rgba(127,191,63,.16) tint.
        //   Turned off → amber dot #F0A94E on rgba(240,169,78,.16) tint.
        if (osGranted)
        {
            NotificationsPillText = "Enabled";
            NotificationsPillBackground = "#297FBF3F";   // green/16
            NotificationsPillForeground = "#7FBF3F";
            NotificationsPillDot = "#7FBF3F";
            NotificationsStatusLine = "BoltMate can notify you. To turn alerts off or change banners and sounds, use System Settings.";
        }
        else
        {
            NotificationsPillText = "Turned off";
            NotificationsPillBackground = "#29F0A94E";   // amber/16
            NotificationsPillForeground = "#F0A94E";
            NotificationsPillDot = "#F0A94E";
            NotificationsStatusLine = "Notifications are turned off in System Settings. Turn them back on there to start getting alerts.";
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
        var receivers = _manager.Receivers.Items.ToList();
        NoReceiver = receivers.Count == 0;
        foreach (var r in receivers)
        {
            foreach (var d in r.Devices.Items.OrderBy(d => (int)d.DeviceIndex))
            {
                if (!d.LinkUp) continue;
                any = true;

                LocalDevices.Add(new LocalDeviceRow
                {
                    Name = d.DisplayName,
                    BatteryPercent = d.LastKnownBattery?.Percent,
                    IsCharging = d.LastKnownBattery?.Charging == true,
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
            PeersEmpty = true;
            return;
        }

        PeersEmpty = false;
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
                        Name = name,
                        BatteryPercent = od.Battery?.Percent,
                        IsCharging = od.Battery?.ExternalPower == true,
                    });
                }
            }
        }

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
        _udpBlocked = h.State == TransportState.Blocked;
        NetworkBlocked = _udpBlocked || _syncBlocked;
    }

    private void UpdateSyncHealth(TransportHealth h)
    {
        SyncEndpoint = h.Endpoint;
        var (label, color) = LabelAndColor(h.State);
        SyncStateLabel = label;
        SyncIndicatorColor = color;
        SyncDetail = $"{label} — {h.DetailMessage}";
        _syncBlocked = h.State == TransportState.Blocked;
        NetworkBlocked = _udpBlocked || _syncBlocked;
    }

    // Status semantic colors mirror DesignTokens.axaml:
    //   Healthy #34C759 · Unknown #C2C2C8 · Blocked dot #FF453A
    // Kept as raw hex strings here so the VM can be exercised without an
    // Avalonia application context (e.g. unit tests). The Status tab XAML
    // could equally bind to brushes by key — string equality with
    // DynamicResource keys would still resolve.
    private static (string Label, string Color) LabelAndColor(TransportState state) => state switch
    {
        TransportState.Healthy => ("Healthy", "#34C759"),
        TransportState.Blocked => ("Blocked", "#FF453A"),
        _ => ("Unknown", "#C2C2C8"),
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

    public sealed class LocalDeviceRow : DeviceRow { }

    public sealed class PeerRow
    {
        public string Header { get; set; } = "";
        public string LastSeen { get; set; } = "";
        public string MetaLine { get; set; } = "";
        public List<PeerDeviceRow> Devices { get; set; } = new();
    }

    public sealed class PeerDeviceRow : DeviceRow { }

    /// <summary>Shared row shape for both local + peer device entries. Matches
    /// the design-handoff §Status row: device name on the left + battery
    /// indicator on the right (pill icon, percent text, optional charging
    /// bolt). When BatteryPercent is null the right side is hidden entirely.
    /// </summary>
    public abstract class DeviceRow
    {
        public string Name { get; set; } = "";

        /// <summary>0..100, or null when we don't know.</summary>
        public int? BatteryPercent { get; set; }

        public bool IsCharging { get; set; }

        public bool HasBattery => BatteryPercent.HasValue;

        public string PercentText => BatteryPercent is { } p ? $"{p}%" : "";

        /// <summary>Width in pixels of the inner fill bar — 0..18px (scaled
        /// from the percent). Bound directly by the row template.</summary>
        public double BatteryFillWidth => BatteryPercent is { } p
            ? Math.Max(2, 18.0 * (p / 100.0))
            : 0;

        /// <summary>Resource key for the row's accent brush. Charging →
        /// green; not-charging → muted secondary. Low-battery treatment
        /// can layer on later.</summary>
        public string AccentBrushKey => IsCharging
            ? "BatteryChargingFillBrush"
            : "TextSecondaryBrush";
    }
}
