using BoltMate.Core.Services;
using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DynamicData;
using BoltMate.App.Services;
using BoltMate.App.UI;
using BoltMate.Core;
using BoltMate.Core.Bolt;
using BoltMate.Core.Permissions;
using BoltMate.Core.Topology;
using BoltMate.Hid.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.App;

public partial class App : Application
{
    /// <summary>
    /// DI container, populated by <see cref="Program.Main"/> before Avalonia
    /// bootstraps. Singletons (PermissionsService, ReceiverManager,
    /// SwitcherService, DeviceEnricher, UpdateService, AppSettings,
    /// ILoggerFactory, IReceiverTransport) are resolved from here. Topology
    /// stack and UI controllers stay manually constructed because they have
    /// runtime lifecycle (toggle / tray-icon-bound) that DI registration
    /// doesn't express cleanly.
    /// </summary>
    public static IServiceProvider Services { get; internal set; } = default!;

    private readonly CompositeDisposable _disposables = new();
    private IReceiverManager? _manager;
    private ISwitcherService? _switcher;
    private TrayMenuController? _trayController;
    private TrayIconStatusController? _trayStatus;
    private UpdateService? _updates;
    private IUdpTopologyService? _topology;
    private IMdnsTcpChannel? _mdnsTcp;
    private ITopologyCorrelator? _correlator;
    private AppSettings _settings = new();
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private IPermissionsService? _permissions;
    private BoltMate.Licensing.ILicenseGate? _licenseGate;
    private BoltMate.Licensing.LicenseStatus _licenseStatus = BoltMate.Licensing.LicenseStatus.NotActivated;

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

        // Pull pre-built dependencies from the container (Program.Main
        // composed it before Avalonia started).
        _loggerFactory = Services.GetRequiredService<ILoggerFactory>();
        _settings = Services.GetRequiredService<AppSettings>();
        var log = _loggerFactory.CreateLogger<App>();
        log.LogInformation("BoltMate.App starting (Avalonia 12)");

        // Hand the platform-specific notification service to the static
        // LocalNotifications facade so the AppHealth callback path + the
        // Settings test-notification button (neither of which has direct
        // access to the DI container at call time) can dispatch via the
        // typed INotificationService. App.Win uses Microsoft.WindowsAppSDK;
        // App.Mac uses Microsoft.macOS bindings over UNUserNotificationCenter.
        LocalNotifications.Service =
            Services.GetService<BoltMate.App.Core.Notifications.INotificationService>();

        // macOS menubar app-name fix. SetProcessName in Program.Main ran
        // before Avalonia bootstrapped, but Avalonia builds NSApp.mainMenu
        // using its own cached title. Rewrite the menu item title here AND
        // again on a deferred dispatch in case Avalonia builds the menu
        // after our framework-init callback returns.
        MacActivationPolicy.SetAppMenuTitle("BoltMate");
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => MacActivationPolicy.SetAppMenuTitle("BoltMate"),
            Avalonia.Threading.DispatcherPriority.Background);

        // Permissions service is a container-managed singleton — same
        // instance flows to the welcome wizard, tray badge, settings status
        // tab. Owns a 2s polling timer; pushes deltas via Rx observables.
        _permissions = Services.GetRequiredService<IPermissionsService>();

        // License gate — single source of truth for "is this user
        // authenticated + entitled". Resolve from DI; the cached JWT
        // (if any) loads on first LoadAsync. We deliberately DON'T
        // block startup on the load: the welcome flow's sign-in page
        // (Phase 4 task #93) will drive ActivateAsync explicitly when
        // the user hits Sign In. The status snapshot stays available
        // via _licenseStatus so the rest of bootstrap can decide
        // whether to route through the sign-in wizard page.
        _licenseGate = Services.GetRequiredService<BoltMate.Licensing.ILicenseGate>();
        _licenseStatus = _licenseGate.Current;
        _disposables.Add(_licenseGate.StatusChanges.Subscribe(s =>
        {
            _licenseStatus = s;
            log.LogInformation("License status → {State} tier={Tier} email={Email}",
                s.State, s.Tier, s.Email ?? "(none)");
        }));
        _ = LoadLicenseAsync(log);

        // Paint the tray icon to match the current OS theme right away.
        // TrayIconStatusController only instantiates inside ContinueBootstrap
        // — during first-run welcome that hasn't happened yet, so without
        // this the XAML-default `tray-icon-light.png` (black bolt) stays
        // shown even on a dark Win taskbar or dark Mac menubar.
        try
        {
            var initialTrays = TrayIcon.GetIcons(this);
            if (initialTrays is not null && initialTrays.Count > 0)
            {
                initialTrays[0].Icon = TrayIconStatusController.LoadNeutralIcon();
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Initial tray icon paint failed (non-fatal)");
        }

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

    /// <summary>
    /// Kicks off the LicenseGate's initial load from secure storage.
    /// Fire-and-forget — the published <see cref="ILicenseGate.Status"/>
    /// observable is the integration surface, and this method's
    /// completion isn't itself a precondition for bootstrap.
    /// </summary>
    private async Task LoadLicenseAsync(ILogger log)
    {
        if (_licenseGate is null) return;
        try
        {
            var status = await _licenseGate.LoadAsync().ConfigureAwait(false);
            log.LogInformation("Initial license load: {State}", status.State);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "License load failed (non-fatal at startup).");
        }
    }

    /// <summary>
    /// Sign-out side: wipes the cached entitlement JWT from the
    /// platform secure store, stops the cross-machine topology stack
    /// so this machine drops off the LAN trust ring immediately, then
    /// reopens the welcome wizard on the new <c>PageSignIn</c> page
    /// so the user can re-auth or close.
    /// </summary>
    private async Task SignOutAndReopenSignInAsync(ILogger log)
    {
        try
        {
            if (_licenseGate is not null)
                await _licenseGate.SignOutAsync().ConfigureAwait(false);
            log.LogInformation("Sign-out complete; cached entitlement wiped.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Sign-out via LicenseGate failed (continuing teardown).");
        }

        // Drop us off the LAN trust ring while signed out so other
        // machines on the same account stop expecting fan-out from
        // this host. Network can be re-enabled after the next sign-in
        // by ApplyTopologySettings.
        try
        {
            _topology?.Stop();
            _mdnsTcp?.Stop();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Topology stop during sign-out threw.");
        }

        // Flip HasShownWelcome back so the wizard treats this like a
        // first-run again (lands on PageSignIn).
        _settings.HasShownWelcome = false;
        try { _settings.Save(); } catch { /* best-effort */ }

        Dispatcher.UIThread.Post(() =>
        {
            if (_welcomeWindow is { IsVisible: true })
            {
                _welcomeWindow.Activate();
                return;
            }
            ShowWelcomeAndDeferStartup(log);
        });
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
            notifications: LocalNotifications.Service,
            licenseGate: _licenseGate,
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
        // HID transport + ReceiverManager are container-managed. Transport
        // selection (OS-specific) happened in Program.Main; ReceiverManager
        // resolves it via DI. Both are container-disposed at app exit, so
        // we don't add them to _disposables.
        _manager = Services.GetRequiredService<IReceiverManager>();

        // A successful receiver attach is proof of the Input Monitoring
        // grant being in place — IOKit wouldn't hand us the device
        // otherwise. Push that empirical fact into the permission
        // service so a flapping IOHIDCheckAccess (TCC's per-process
        // cache occasionally goes Unknown after hours of runtime)
        // can't downgrade IsGranted and trip a false "Fix permissions"
        // alert while we're literally talking to the device.
        if (_permissions is not null && OperatingSystem.IsMacOS())
        {
            _disposables.Add(_manager.Receivers.CountChanged
                .Where(c => c > 0)
                .Subscribe(_ => _permissions.InputMonitoring.AcknowledgeExternalGrant()));
        }

        // macOS USB hot-plug — start the IOServiceAddMatchingNotification
        // watcher and route its signal to ReceiverManager.Refresh() so
        // attaches / detaches surface sub-second instead of waiting for
        // the 2s timer tick. Refresh runs on the .NET thread pool, NOT on
        // the notifier's CFRunLoop thread (the notifier callback itself
        // does zero IOKit work — see UsbBoltNotifier xmldoc + memory).
        if (OperatingSystem.IsMacOS())
        {
            var usbNotifier = Services.GetRequiredService<BoltMate.Hid.IOKit.UsbBoltNotifier>();
            usbNotifier.Start();
            _disposables.Add(usbNotifier.Changes
                .Subscribe(_ => Task.Run(() =>
                {
                    try { _manager.Refresh(); }
                    catch (Exception ex) { log.LogWarning(ex, "Refresh on USB notification failed"); }
                })));
            _disposables.Add(usbNotifier);
        }

        // One manager-scoped SwitcherService handles fan-out across every
        // attached receiver. Matches siblings by host friendly name (the
        // OS hostname recorded in each device's host slot at pairing time).
        _switcher = Services.GetRequiredService<SwitcherService>();

        // UDP topology — LAN broadcast of receiver state + cross-machine
        // correlator. Opt-in via Settings → Network. Live-toggle: enable /
        // disable in Settings starts / stops the UDP socket immediately, no
        // app restart needed.
        ApplyTopologySettings();

        // Auto-enrich devices on link-up (feature discovery, name, battery,
        // host bindings). Resolve once to bind the manager subscription.
        // No on-disk persistence — bindings are re-read on every link-up
        // since the host name we match on is provided fresh by the device.
        _ = Services.GetRequiredService<DeviceEnricher>();

        // Update check scaffold — fires once at startup when auto-check is on.
        // Real cast endpoint lives behind UpdateService.CheckAsync (currently
        // a no-op stub that only stamps LastUpdateCheckUtc).
        _updates = Services.GetRequiredService<UpdateService>();
        if (_settings.AutoCheckForUpdates)
            _ = _updates.CheckAsync();

        // Wire the dynamic tray menu now that the manager is up.
        var trays = TrayIcon.GetIcons(this);
        if (trays is { Count: > 0 } && trays[0].Menu is { } menu)
        {
            _trayController = new TrayMenuController(menu, _manager, _permissions!,
                _loggerFactory.CreateLogger<TrayMenuController>())
            {
                OnStatusClicked = () => OpenSettings(SettingsWindow.TabStatus),
                OnAboutClicked = () => OpenSettings(SettingsWindow.TabGeneral),
                OnLicenseClicked = () => OpenSettings(SettingsWindow.TabLicense),
                OnFixPermissionsClicked = OpenWelcomeToFirstUngranted,
                OnSignOutClicked = () => _ = SignOutAndReopenSignInAsync(log),
            };
            _disposables.Add(_trayController);

            // Tray icon owns its image + theme + connection-health badge.
            _trayStatus = new TrayIconStatusController(trays[0], _permissions!,
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
        // AppHealthService is now DI-resolved. Wire its observable to the
        // tray + OS notification side-effects from here so the service
        // itself stays pure.
        _health = Services.GetRequiredService<IAppHealthService>();
        _disposables.Add(_health.Health.Subscribe(snapshot =>
        {
            var status = snapshot.IsAlerting ? OverallStatus.AnyDenied : OverallStatus.AllGood;
            _trayStatus?.SetPermissionStatus(status);
            _trayStatus?.SetHealth(snapshot);
            _trayController?.SetPermissionStatus(status);
        }));

        // One-shot toast per fresh alert. Track which categories we've
        // already notified about so we don't re-toast on every tick
        // while the condition persists; clear acknowledgement when the
        // category resolves so a re-occurrence re-notifies.
        var notified = new System.Collections.Generic.HashSet<string>();
        _disposables.Add(_health.Health.Subscribe(snapshot =>
        {
            void MaybeToast(CategoryAlert? c)
            {
                if (c is null) return;
                if (!notified.Add(c.Category)) return;
                var ok = LocalNotifications.TryPost($"BoltMate · {c.Category} issue", c.Detail);
                if (ok) log.LogInformation("Health notification delivered: {Title}", c.Category);
                else log.LogDebug("Health notification not posted (tray fallback): {Title}", c.Category);
            }
            MaybeToast(snapshot.Permissions);
            MaybeToast(snapshot.Network);
            MaybeToast(snapshot.Receiver);
            if (snapshot.Permissions is null) notified.Remove("Permissions");
            if (snapshot.Network is null) notified.Remove("Network");
            if (snapshot.Receiver is null) notified.Remove("Receiver");
        }));
    }

    private IAppHealthService? _health;

    /// <summary>
    /// Opens the WelcomeWindow positioned at the first permission that is
    /// currently denied (or unknown if no Denied exists). Used by the tray
    /// "Fix permissions…" item and the notification click.
    /// </summary>
    private void OpenWelcomeToFirstUngranted()
    {
        // Re-poll BEFORE deciding — the alert that triggered this click may
        // be stale (CategoryTracker only re-evaluates on a 1Hz tick or on
        // input change). A spurious "Fix permissions" tap shouldn't drag
        // the user into the wizard if everything is actually fine.
        (_permissions as PermissionsService)?.Refresh();
        if (_permissions is not null
            && _permissions.Network.IsGranted
            && _permissions.InputMonitoring.IsGranted)
        {
            _trayStatus?.SetPermissionStatus(OverallStatus.AllGood);
            _trayController?.SetPermissionStatus(OverallStatus.AllGood);
            return;
        }

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
                _permissions ?? new PermissionsService(notifications: null, _loggerFactory),
                isFirstRun: false,
                notifications: LocalNotifications.Service,
                licenseGate: _licenseGate,
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
    /// Starts or stops the topology services to match
    /// <see cref="AppSettings.Topology"/>. All three (UDP / mDNS+TCP /
    /// correlator) are DI singletons now — toggle is just Start/Stop,
    /// not new/Dispose. Safe to call repeatedly.
    /// </summary>
    private bool _topologyOneTimeWired;

    private void ApplyTopologySettings()
    {
        if (_manager is null || _switcher is null) return;

        _topology    ??= Services.GetRequiredService<IUdpTopologyService>();
        _mdnsTcp     ??= Services.GetRequiredService<IMdnsTcpChannel>();
        _correlator  ??= Services.GetRequiredService<ITopologyCorrelator>();

        if (!_settings.Topology.Enabled)
        {
            _topology.Stop();
            _mdnsTcp.Stop();
            _trayController?.Bind(null);
            _trayStatus?.Bind(null);
            _trayStatus?.BindHealth(null, null);
            _settingsWindow?.BindHealth(null, null);
            return;
        }

        _topology.Start();
        _mdnsTcp.Start();
        _trayController?.Bind(_topology);
        _trayStatus?.Bind(_topology);

        // One-time wiring that lives across enable/disable cycles —
        // attaching to long-lived observables that never change. Re-
        // running this on every toggle would stack duplicate subscribers.
        if (!_topologyOneTimeWired)
        {
            _topologyOneTimeWired = true;

            // A Healthy UDP self-echo means the LAN-broadcast roundtrip ran
            // successfully — proof the Local Network grant is in place even
            // when the OS-level probe has gone Unknown. Same cure for the
            // same TCC-cache flap that hits Input Monitoring.
            if (_permissions is not null)
            {
                _disposables.Add(_topology.UdpHealth
                    .Where(h => h.State is BoltMate.Core.Topology.TransportState.Healthy)
                    .Take(1)
                    .Subscribe(_ => _permissions.Network.AcknowledgeExternalGrant()));
            }

            // Local switch trigger → broadcast intent to peers. Fires once
            // per Easy-Switch press / Flow snoop / user request, BEFORE
            // local fan-out, regardless of whether the local machine has
            // siblings to switch. RemoteTopology source is suppressed at
            // the Core layer so peer rebroadcasts don't loop.
            var topology = _topology;
            _disposables.Add(_switcher.LocalSwitchTriggers.Subscribe(t =>
            {
                topology.RecordLocalSwitchEvent(t.OriginatingDeviceSerial, t.TargetHostName);
            }));
        }

        _trayStatus?.BindHealth(_topology.UdpHealth, _mdnsTcp.SyncHealth);
        _settingsWindow?.BindHealth(_topology.UdpHealth, _mdnsTcp.SyncHealth);
    }

    private void OpenSettings(string? initialTab = null)
    {
        if (_manager is null) return;

        // Lazy-construct the SettingsWindow on first open. The Closing handler
        // inside it cancels-and-hides, so subsequent opens reuse this instance
        // and are instant.
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_manager, _settings, _permissions!, LocalNotifications.Service, _topology?.UdpHealth, _mdnsTcp?.SyncHealth)
            {
                HostNamesChanged = () => _trayController?.RefreshHostLabels(),
                TopologyChanged = ApplyTopologySettings,
                PeerAnnouncementsProvider = () =>
                    _topology is null
                        ? Array.Empty<BoltMate.Core.Topology.ReceiverAnnouncement>()
                        : _topology.LatestPeerAnnouncements,
                PeerStatsProvider = () =>
                    _topology is null
                        ? Array.Empty<BoltMate.Core.Services.PeerStats>()
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
