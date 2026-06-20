using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DynamicData;
using BoltMate.App.Updates;
using BoltMate.App.Welcome;
using BoltMate.Core;
using BoltMate.Core.Bolt;
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
    private PermissionStatusService? _permissions;

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

        // macOS menubar app-name fix when launched without a .app bundle (dev
        // `dotnet run`). Bundled .app gets this from Info.plist already.
        MacActivationPolicy.SetProcessName("BoltMate");

        _settings = AppSettings.Load();
        _settings.Topology.Enabled = true;

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

        _welcomeWindow = new WelcomeWindow(_settings);
        _welcomeWindow.WelcomeCompleted += () =>
        {
            // After the wizard finishes, run normal bootstrap THEN open the
            // Settings window to the Status tab. The Settings window is
            // pre-warmed inside ContinueBootstrap so OpenSettings is instant.
            Dispatcher.UIThread.Post(() =>
            {
                ContinueBootstrap(log);
                MacActivationPolicy.HideDockIcon();
                OpenSettings(SettingsWindow.TabStatus);
            });
        };
        _welcomeWindow.Show();
        _welcomeWindow.Activate();
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

        // Hydrate cached receiver identifiers on attach + persist updates
        // after GetReceiverDetailsAsync succeeds. Without this, a freshly
        // launched app on a host that hasn't read its receiver-level
        // identifier yet (or where the read fails) can't include a usable
        // HostIdentifierHex in topology announcements, breaking cross-
        // machine MATCH for devices arriving on this host.
        _disposables.Add(_manager.Receivers.Connect()
            .Subscribe(changes =>
            {
                foreach (var change in changes)
                {
                    if (change.Reason != DynamicData.ChangeReason.Add) continue;
                    var r = change.Current;
                    if (_settings.CachedReceiverIdentifiers.TryGetValue(r.Info.Path, out var cachedHex)
                        && !string.IsNullOrEmpty(cachedHex))
                    {
                        try { r.HostIdentifier = Convert.FromHexString(cachedHex); }
                        catch { /* malformed cache — ignore */ }
                    }
                    // Periodically sample the receiver's effective identifier
                    // and persist it. "Effective" = receiver-level read OR
                    // the value any connected device has stored for its
                    // current host slot (same per-pairing identifier from a
                    // different read path). Re-poll every few seconds for ~30s
                    // so we catch slow-arriving HostBindings reads on Win.
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        for (var i = 0; i < 10; i++)
                        {
                            await System.Threading.Tasks.Task.Delay(3000);
                            string? effective = r.HostIdentifierKey;
                            if (effective is null)
                            {
                                foreach (var d in r.Devices.Items)
                                {
                                    if (!d.LinkUp) continue;
                                    if (d.LastKnownCurrentHost is not byte cur) continue;
                                    if (d.HostBindings.TryGetValue(cur, out var binding)
                                        && binding.HostIdentifierKey is { } v)
                                    { effective = v; break; }
                                }
                            }
                            if (effective is not null && effective != cachedHex)
                            {
                                _settings.CachedReceiverIdentifiers[r.Info.Path] = effective;
                                try { _settings.Save(); } catch { }
                                // Also pin onto receiver itself for immediate use by the
                                // announcement builder, in case GetReceiverDetailsAsync
                                // never actually returned a value.
                                if (r.HostIdentifierKey is null)
                                {
                                    try { r.HostIdentifier = Convert.FromHexString(effective); }
                                    catch { }
                                }
                                cachedHex = effective;
                                break;
                            }
                        }
                    });
                }
            }));

        _disposables.Add(_manager);
        _disposables.Add(_loggerFactory);

        // One manager-scoped SwitcherService handles fan-out across every
        // attached receiver. Topology-aware: routes by matching BLE address
        // through each device's HostBindings.
        _switcher = new SwitcherService(_manager, _loggerFactory.CreateLogger<SwitcherService>());
        _disposables.Add(_switcher);

        // UDP topology — LAN broadcast of receiver state + cross-machine
        // correlator. Opt-in via Settings → Network. Live-toggle: enable /
        // disable in Settings starts / stops the UDP socket immediately, no
        // app restart needed.
        ApplyTopologySettings();

        // Auto-enrich devices on link-up (feature discovery, name, battery, host bindings).
        _disposables.Add(new DeviceEnricher(_manager, _loggerFactory.CreateLogger<DeviceEnricher>()));

        // Persist host bindings to disk so offline-device topology survives restarts.
        _disposables.Add(new HostBindingPersistence(_manager, _settings,
            _loggerFactory.CreateLogger<HostBindingPersistence>()));

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

        // Pre-warm the SettingsWindow so the first user-triggered open is
        // instant. We never tear the window down between open / close —
        // the Closing handler in SettingsWindow cancels the close and hides
        // the window instead. Permanent teardown happens via Hibernate() in
        // the desktop Exit handler below.
        _settingsWindow = new SettingsWindow(_manager, _settings)
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
        _permissions = new PermissionStatusService();
        _disposables.Add(_permissions);
        _permissions.Start();

        _disposables.Add(_permissions.Observe()
            .Subscribe(s => Dispatcher.UIThread.Post(() => OnPermissionStatusChanged(s, log))));
    }

    private void OnPermissionStatusChanged(PermissionStatus s, ILogger log)
    {
        _trayStatus?.SetPermissionStatus(s.Overall);
        _trayController?.SetPermissionStatus(s.Overall);

        if (s.Overall == OverallStatus.AnyDenied && !_notificationDeliveredThisSession)
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
        var snap = _permissions?.Current ?? default;
        string primerId = WelcomeWindow.PermissionNetwork;
        if (snap.Network == PermissionState.Denied || snap.Network == PermissionState.Unknown)
            primerId = WelcomeWindow.PermissionNetwork;
        else if (snap.InputMonitoring == PermissionState.Denied || snap.InputMonitoring == PermissionState.Unknown)
            primerId = WelcomeWindow.PermissionInputMonitoring;

        if (_welcomeWindow is null || !_welcomeWindow.IsVisible)
        {
            _welcomeWindow = new WelcomeWindow(_settings);
            // Don't flip HasShownWelcome here — this is a "fix" run, not a
            // first run. Just open at the primer and let the user trigger /
            // skip as they wish. We DO NOT subscribe to WelcomeCompleted
            // because bootstrap is already done.
        }
        MacActivationPolicy.ShowDockIcon();
        _welcomeWindow.OpenToPrimer(primerId);
        _welcomeWindow.Show();
        _welcomeWindow.Activate();
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
        if (_settingsWindow is null) return;

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
