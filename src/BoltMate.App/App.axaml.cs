using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DynamicData;
using BoltMate.App.Permissions;
using BoltMate.App.Updates;
using BoltMate.App.Welcome;
using BoltMate.Core;
using BoltMate.Core.Bolt;
using BoltMate.Core.Permissions;
using BoltMate.Core.Switcher;
using BoltMate.Core.Topology;
using BoltMate.Hid.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.App;

public partial class App : Application
{
    private readonly CompositeDisposable _disposables = new();
    private ReceiverManager? _manager;
    private SwitcherService? _switcher;
    private TrayMenuController? _trayController;
    private TrayIconStatusController? _trayStatus;
    private UpdateService? _updates;
    private UdpTopologyService? _topology;
    private MdnsTcpChannel? _mdnsTcp;
    private TopologyCorrelator? _correlator;
    private AppSettings _settings = new();
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private IPermissionsService? _permissions;

    // Tracks whether we have already nagged the user with a notification
    // during THIS run. Re-fires after a relaunch; we deliberately don't
    // persist this so the user gets a fresh prompt every session if the
    // permission stays denied.
    private bool _notificationDeliveredThisSession;

    // Owned by App so the tray "Fix permissions…" item + the local
    // notification click handler can reopen it on demand even after
    // first run. Created lazily.
    private WelcomeWindow? _welcomeWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            OnFrameworkInitializationCompletedCore();
        }
        catch (Exception ex)
        {
            // Last-resort log to the user-visible logs dir so silent
            // startup crashes are diagnosable from the field.
            try
            {
                var dir = AppPaths.LogsDirectory;
                System.IO.Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, $"boltmate-crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                System.IO.File.WriteAllText(path,
                    $"BoltMate.App startup crash @ {DateTime.UtcNow:O}\n\n{ex}\n");
            }
            catch { /* swallow — best effort */ }
            throw;
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void OnFrameworkInitializationCompletedCore()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => _disposables.Dispose();
        }

        _loggerFactory = AppLoggerSetup.Create();
        var log = _loggerFactory.CreateLogger<App>();
        log.LogInformation("BoltMate.App starting (Avalonia 12)");

        // macOS menubar app-name fix. SetProcessName in Program.Main ran
        // before Avalonia bootstrapped, but Avalonia builds NSApp.mainMenu
        // using its own cached title. Rewrite the menu item title here AND
        // again on a deferred dispatch in case Avalonia builds the menu
        // after our framework-init callback returns.
        MacActivationPolicy.SetAppMenuTitle("BoltMate");
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => MacActivationPolicy.SetAppMenuTitle("BoltMate"),
            Avalonia.Threading.DispatcherPriority.Background);

        _settings = AppSettings.Load();
        _settings.Topology.Enabled = true;

        // Permissions service is constructed once per process and shared by
        // every consumer (welcome wizard, tray badge, settings status tab).
        // Owns a 2s polling timer; pushes deltas to per-permission observables.
        _permissions = new PermissionsService(_loggerFactory);

        // ====================================================================
        // First-run gate
        // ====================================================================
        //
        // If the user has never seen the welcome wizard, run it BEFORE any HID
        // transport / topology service is started. Two reasons:
        //   1. We want the Local Network + Input Monitoring TCC prompts to be
        //      explicitly tied to a primer screen, NOT fire unannounced from
        //      a background HID probe.
        //   2. The HID transport on macOS goes through IOKit and reading
        //      anything will immediately trigger the Input Monitoring prompt,
        //      defeating the priming UX.
        if (!_settings.HasShownWelcome)
        {
            ShowWelcomeAndDeferStartup(log);
            return;
        }

        // Subsequent launches: run permission check on a background task and
        // continue startup. If anything is denied, the tray badge + menu +
        // local notification surface it without blocking startup.
        ContinueBootstrap(log);
    }

    // ====================================================================
    // First-run welcome → bootstrap chain
    // ====================================================================

    private void ShowWelcomeAndDeferStartup(ILogger log)
    {
        log.LogInformation("First-run welcome wizard");
        // Dock icon visible for the duration of the wizard so users can
        // Cmd-Tab into it.
        MacActivationPolicy.ShowDockIcon();

        _welcomeWindow = new WelcomeWindow(_settings,
            _permissions!,
            isFirstRun: true,
            log: _loggerFactory.CreateLogger<WelcomeWindow>());
        _welcomeWindow.WelcomeCompleted += () =>
        {
            // After the wizard finishes, run normal bootstrap THEN open the
            // Settings window to the Status tab.
            Dispatcher.UIThread.Post(() =>
            {
                // Release MainWindow tether so closing Settings doesn't
                // shutdown the tray-only app.
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
                    d.MainWindow = null;
                ContinueBootstrap(log);
                MacActivationPolicy.HideDockIcon();
                OpenSettings(SettingsWindow.TabStatus);
            });
        };

        // Windows: Avalonia's Win32 backend treats MainWindow specially —
        // setting it before Show() guarantees the OS surfaces the window
        // and gives it activation. Without this, the welcome window can be
        // dropped on the floor by Avalonia's startup race on Win 11.
        // (ShutdownMode is OnExplicitShutdown so closing MainWindow won't
        // tear down the app even if we forget to clear it.)
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = _welcomeWindow;
        }

        // Defer Show() until after framework-init returns and the message
        // loop is running. On Windows this is required — calling Show()
        // synchronously inside OnFrameworkInitializationCompleted silently
        // drops the window because the desktop lifetime hasn't finished
        // registering top-level window hosting yet.
        Dispatcher.UIThread.Post(() =>
        {
            _welcomeWindow.Show();
            // Brief Topmost flicker forces Win + macOS to bring the window
            // to the foreground even if some other process has stolen focus
            // (e.g. a recently launched installer post-install).
            _welcomeWindow.Topmost = true;
            _welcomeWindow.Activate();
            Dispatcher.UIThread.Post(() => _welcomeWindow.Topmost = false,
                DispatcherPriority.Background);

            _welcomeWindow.RunFlow();
        }, DispatcherPriority.Loaded);
    }

    private void ContinueBootstrap(ILogger log)
    {
        // Composition root for the HID transport.
        // macOS: IOKit-direct (libhidapi 0.15.0 ignores shared-access flag,
        //        breaks device firmware buttons — see project_mac_hid_open_breaks_device
        //        memory).
        // Windows: native Win32 HID via setupapi + hid.dll (eliminates HidApi.Net
        //          long-report write failures and arm64-vs-x64 emulation issues).
        // Linux: HidApi.Net (libhidapi on Linux works fine with our usage).
        IReceiverTransport transport =
            OperatingSystem.IsMacOS()   ? new BoltMate.Hid.IOKit.IOKitReceiverTransport(_loggerFactory) :
            OperatingSystem.IsWindows() ? new BoltMate.Hid.Win.WinReceiverTransport(_loggerFactory) :
                                          new BoltMate.Hid.HidApi.HidApiReceiverTransport(_loggerFactory);
        _manager = new ReceiverManager(transport, loggerFactory: _loggerFactory);
        _disposables.Add(_manager);
        _disposables.Add(_loggerFactory);

        // One manager-scoped SwitcherService handles fan-out across every
        // attached receiver. Matches siblings by host friendly name (the
        // OS hostname recorded in each device's host slot at pairing time).
        _switcher = new SwitcherService(_manager, _loggerFactory.CreateLogger<SwitcherService>());
        _disposables.Add(_switcher);

        // UDP topology — LAN broadcast of receiver state + cross-machine
        // correlator. Opt-in via Settings → Network. Live-toggle: enable /
        // disable in Settings starts / stops the UDP socket immediately, no
        // app restart needed.
        ApplyTopologySettings();

        // Auto-enrich devices on link-up (feature discovery, name, battery, host bindings).
        // No on-disk persistence — bindings are re-read on every link-up since the
        // host name we match on is provided fresh by the device each time.
        _disposables.Add(new DeviceEnricher(_manager, _loggerFactory.CreateLogger<DeviceEnricher>()));

        // Update check scaffold — fires once at startup when auto-check is on.
        // Real cast endpoint lives behind UpdateService.CheckAsync (currently
        // a no-op stub that only stamps LastUpdateCheckUtc).
        _updates = new UpdateService(_settings, _loggerFactory.CreateLogger<UpdateService>());
        if (_settings.AutoCheckForUpdates)
            _ = _updates.CheckAsync();

        // Wire the dynamic tray menu now that the manager is up.
        var trays = TrayIcon.GetIcons(this);
        if (trays is { Count: > 0 } && trays[0].Menu is { } menu)
        {
            _trayController = new TrayMenuController(menu, _manager,
                _loggerFactory.CreateLogger<TrayMenuController>())
            {
                OnStatusClicked = () => OpenSettings(SettingsWindow.TabStatus),
                OnAboutClicked = () => OpenSettings(SettingsWindow.TabAbout),
                OnLicenseClicked = () => OpenSettings(SettingsWindow.TabLicense),
                OnFixPermissionsClicked = OpenWelcomeToFirstUngranted,
            };
            _disposables.Add(_trayController);

            // Tray icon owns its image + theme + connection-health badge.
            _trayStatus = new TrayIconStatusController(trays[0],
                _loggerFactory.CreateLogger<TrayIconStatusController>());
            _disposables.Add(_trayStatus);

            // Windows tray convention: left-click opens the settings window
            // directly (mirrors what the OS does for Action-Center–style icons).
            // The context menu still appears on right-click via the Menu binding.
            // macOS leaves the click handler unbound: in the menubar, clicking
            // an icon SHOULD show its menu — that's the native behavior.
            if (OperatingSystem.IsWindows())
            {
                trays[0].Clicked += (_, _) => OpenSettings(SettingsWindow.TabStatus);
            }
        }
        else
        {
            log.LogWarning("TrayIcon not found at framework init; dynamic menu wiring skipped");
        }

        // SettingsWindow is constructed lazily on first OpenSettings() call.
        // The pre-warm pattern (construct here, keep alive) caused a visible
        // window-flash on Windows because Avalonia briefly realizes the OS
        // window even when Show() is never called. The Closing-handler in
        // SettingsWindow still cancels-and-hides after first open, so all
        // subsequent opens remain instant — only the very first is slow.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLife)
        {
            desktopLife.Exit += (_, _) => _settingsWindow?.Hibernate();
        }

        // ====================================================================
        // Permission watchdog — runs continuously, drives tray badge + alert
        // notification on subsequent launches.
        // ====================================================================
        StartPermissionWatchdog(log);
    }

    private SettingsWindow? _settingsWindow;

    private void StartPermissionWatchdog(ILogger log)
    {
        // Service is constructed earlier in OnFrameworkInitializationCompletedCore
        // (so the welcome wizard can use it). Watchdog is the consumer that
        // wires permission state into the tray badge + notification.
        if (_permissions is null) return;
        _disposables.Add(_permissions);

        var combined = _permissions.Network.IsGrantedChanged
            .CombineLatest(_permissions.InputMonitoring.IsGrantedChanged,
                (net, im) => (net && im) ? OverallStatus.AllGood : OverallStatus.AnyDenied)
            .DistinctUntilChanged();

        _disposables.Add(combined.Subscribe(overall =>
            Dispatcher.UIThread.Post(() => OnPermissionOverallChanged(overall, log))));
    }

    private void OnPermissionOverallChanged(OverallStatus overall, ILogger log)
    {
        _trayStatus?.SetPermissionStatus(overall);
        _trayController?.SetPermissionStatus(overall);

        if (overall == OverallStatus.AnyDenied && !_notificationDeliveredThisSession)
        {
            _notificationDeliveredThisSession = true;
            var posted = LocalNotifications.TryPost(
                "BoltMate needs permissions",
                "Click to fix");
            if (posted)
                log.LogInformation("Permission alert notification delivered");
            else
                log.LogDebug("Permission alert notification not posted (platform fallback to tray-only)");
        }
    }

    /// <summary>
    /// Opens the WelcomeWindow positioned at the first permission that is
    /// currently denied (or unknown if no Denied exists). Used by the tray
    /// "Fix permissions…" item and the notification click.
    /// </summary>
    private void OpenWelcomeToFirstUngranted()
    {
        string primerId = WelcomeWindow.PermissionNetwork;
        if (_permissions is not null)
        {
            if (!_permissions.Network.IsGranted)
                primerId = WelcomeWindow.PermissionNetwork;
            else if (!_permissions.InputMonitoring.IsGranted)
                primerId = WelcomeWindow.PermissionInputMonitoring;
        }

        if (_welcomeWindow is null || !_welcomeWindow.IsVisible)
        {
            _welcomeWindow = new WelcomeWindow(_settings,
                _permissions ?? new PermissionsService(_loggerFactory),
                isFirstRun: false,
                log: _loggerFactory.CreateLogger<WelcomeWindow>());
            // Don't flip HasShownWelcome here — this is a "fix" run, not a
            // first run. Just open at the primer and let the user trigger /
            // skip as they wish. We DO NOT subscribe to WelcomeCompleted
            // because bootstrap is already done.
        }
        MacActivationPolicy.ShowDockIcon();
        _welcomeWindow.OpenToPrimer(primerId);
        _welcomeWindow.Show();
        _welcomeWindow.Topmost = true;
        _welcomeWindow.Activate();
        Dispatcher.UIThread.Post(() => _welcomeWindow.Topmost = false,
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Starts or stops the UDP topology services to match
    /// <see cref="AppSettings.Topology"/>. Safe to call repeatedly — disposes
    /// existing services before recreating. Wired to the Settings -> Network
    /// toggle so the user doesn't have to restart.
    /// </summary>
    private void ApplyTopologySettings()
    {
        if (_manager is null || _switcher is null) return;

        // Tear down anything currently running.
        if (_correlator is not null)
        {
            _disposables.Remove(_correlator);
            _correlator.Dispose();
            _correlator = null;
        }
        if (_mdnsTcp is not null)
        {
            _disposables.Remove(_mdnsTcp);
            _mdnsTcp.Dispose();
            _mdnsTcp = null;
        }
        if (_topology is not null)
        {
            _disposables.Remove(_topology);
            _topology.Dispose();
            _topology = null;
        }

        if (!_settings.Topology.Enabled)
        {
            _trayController?.Bind(null);
            _trayStatus?.Bind(null);
            return;
        }

        var machineId = _settings.Topology.MachineId;
        if (string.IsNullOrWhiteSpace(machineId))
        {
            machineId = Guid.NewGuid().ToString("N");
            _settings.Topology.MachineId = machineId;
            _settings.Save();
        }

        _topology = new UdpTopologyService(_manager, _settings.Topology, machineId,
            _loggerFactory.CreateLogger<UdpTopologyService>());
        _topology.Start();
        _disposables.Add(_topology);
        _trayController?.Bind(_topology);
        _trayStatus?.Bind(_topology);

        if (_settings.Topology.UseMdnsTcp)
        {
            _mdnsTcp = new MdnsTcpChannel(_topology, _settings.Topology, machineId,
                _loggerFactory.CreateLogger<MdnsTcpChannel>());
            _mdnsTcp.Start();
            _disposables.Add(_mdnsTcp);
        }

        _correlator = new TopologyCorrelator(_manager, _switcher,
            _topology.Announcements,
            TimeSpan.FromSeconds(_settings.Topology.CorrelationWindowSeconds),
            _loggerFactory.CreateLogger<TopologyCorrelator>());
        _disposables.Add(_correlator);
    }

    private void OpenSettings(string? initialTab = null)
    {
        if (_manager is null) return;

        // Lazy-construct the SettingsWindow on first open. The Closing handler
        // inside it cancels-and-hides, so subsequent opens reuse this instance
        // and are instant.
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_manager, _settings, _permissions!)
            {
                HostNamesChanged = () => _trayController?.RefreshHostLabels(),
                TopologyChanged = ApplyTopologySettings,
                PeerAnnouncementsProvider = () =>
                    _topology is null
                        ? Array.Empty<BoltMate.Core.Topology.ReceiverAnnouncement>()
                        : _topology.LatestPeerAnnouncements,
                PeerStatsProvider = () =>
                    _topology is null
                        ? Array.Empty<BoltMate.Core.Topology.PeerStats>()
                        : _topology.PeerSnapshot,
                SendStatsProvider = () =>
                    _topology is null ? (0L, 0L) : _topology.SendStats,
                OnHidden = () => MacActivationPolicy.HideDockIcon(),
            };
        }

        // Show the dock icon for the duration of the settings window so users
        // who Cmd-Tab can find us. Restore accessory mode in OnHidden.
        MacActivationPolicy.ShowDockIcon();

        if (initialTab is not null) _settingsWindow.OpenTo(initialTab);

        if (!_settingsWindow.IsVisible)
            _settingsWindow.Show();

        _settingsWindow.Activate();
        _settingsWindow.BringIntoView();
    }

}
