using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DynamicData;
using LogiPlusSwitcher.App.Hotkeys;
using LogiPlusSwitcher.App.Licensing;
using LogiPlusSwitcher.App.Updates;
using Avalonia.Threading;
using LogiPlusSwitcher.Core;
using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Core.Switcher;
using LogiPlusSwitcher.Core.Topology;
using LogiPlusSwitcher.Hid.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogiPlusSwitcher.App;

public partial class App : Application
{
    private readonly CompositeDisposable _disposables = new();
    private ReceiverManager? _manager;
    private SwitcherService? _switcher;
    private TrayMenuController? _trayController;
    private ReceiverPolicyService? _policy;
    private UpdateService? _updates;
    private IGlobalHotkeyService? _hotkeyService;
    private HotkeyOrchestrator? _hotkeys;
    private UdpTopologyService? _topology;
    private TopologyCorrelator? _correlator;
    private AppSettings _settings = new();
    private ILicenseService _license = new DevAlwaysProLicenseService();
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => _disposables.Dispose();
        }

        _loggerFactory = AppLoggerSetup.Create();
        var log = _loggerFactory.CreateLogger<App>();
        log.LogInformation("LogiPlusSwitcher.App starting (Avalonia 12)");

        _settings = AppSettings.Load();
        // Composition root for the HID transport.
        // macOS uses IOKit-direct (libhidapi 0.15.0 ignores shared-access flag,
        // breaks device firmware buttons — see project_mac_hid_open_breaks_device
        // memory). Win/Linux use the libhidapi-backed transport.
        IReceiverTransport transport = OperatingSystem.IsMacOS()
            ? new LogiPlusSwitcher.Hid.IOKit.IOKitReceiverTransport(_loggerFactory)
            : new LogiPlusSwitcher.Hid.HidApi.HidApiReceiverTransport(_loggerFactory);
        _manager = new ReceiverManager(transport, loggerFactory: _loggerFactory);

        _disposables.Add(_manager);
        _disposables.Add(_loggerFactory);

        // Tier policy: drives BoltReceiver.IsParticipating based on
        // license + primary-receiver setting + attached set.
        _policy = new ReceiverPolicyService(_manager, _license, _settings,
            _loggerFactory.CreateLogger<ReceiverPolicyService>());
        _disposables.Add(_policy);
        _disposables.Add(_policy.MultiReceiverPromptRequired
            .Subscribe(_ => Dispatcher.UIThread.Post(ShowMultiReceiverPrompt)));

        // One manager-scoped SwitcherService handles fan-out across every
        // attached receiver. Topology-aware: routes by matching BLE address
        // through each device's HostBindings.
        _switcher = new SwitcherService(_manager, _loggerFactory.CreateLogger<SwitcherService>());
        _disposables.Add(_switcher);

        // Global hotkey support. macOS uses Carbon RegisterEventHotKey; Win
        // uses RegisterHotKey on a message-only window. Both let the user
        // bypass the physical Easy-Switch button (which the keyboard handles
        // internally before any software can see it — see
        // project_easyswitch_firmware_direct memory for the proof).
        _hotkeyService = GlobalHotkeyServiceFactory.Create(_loggerFactory);
        _disposables.Add(_hotkeyService);
        _hotkeys = new HotkeyOrchestrator(_hotkeyService, _switcher, _settings.Hotkeys,
            _loggerFactory.CreateLogger<HotkeyOrchestrator>());
        _hotkeys.Apply();
        _disposables.Add(_hotkeys);

        // UDP topology — LAN broadcast of receiver state + cross-machine
        // correlator. Opt-in via Settings → Network. When a Bolt device
        // disconnects locally and re-appears on a peer's announcement within
        // the correlation window, fan out remaining local devices to follow.
        if (_settings.Topology.Enabled)
        {
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
            _correlator = new TopologyCorrelator(_manager, _switcher,
                _topology.Announcements,
                TimeSpan.FromSeconds(_settings.Topology.CorrelationWindowSeconds),
                _loggerFactory.CreateLogger<TopologyCorrelator>());
            _disposables.Add(_correlator);
        }

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
            _trayController = new TrayMenuController(menu, _manager, _license,
                _loggerFactory.CreateLogger<TrayMenuController>(), _settings)
            {
                OnSettingsClicked = OpenSettings,
                OnCheckForUpdatesClicked = () => _ = CheckForUpdatesNowAsync(),
                OnAboutClicked = ShowAbout,
            };
            _disposables.Add(_trayController);

            // Theme-aware tray icon — black on light menubars, white on dark.
            _disposables.Add(new TrayIconThemeWatcher(trays[0],
                _loggerFactory.CreateLogger<TrayIconThemeWatcher>()));
        }
        else
        {
            log.LogWarning("TrayIcon not found at framework init; dynamic menu wiring skipped");
        }

        base.OnFrameworkInitializationCompleted();
    }

    private SettingsWindow? _settingsWindow;

    private void OpenSettings()
    {
        if (_manager is null || _policy is null) return;

        if (_settingsWindow is not null && _settingsWindow.IsVisible)
        {
            _settingsWindow.Activate();
            return;
        }

        // Show the dock icon for the duration of the settings window so users
        // who Cmd-Tab can find us. Restore accessory mode on close.
        MacActivationPolicy.ShowDockIcon();

        _settingsWindow = new SettingsWindow(_manager, _policy, _license, _settings)
        {
            HostNamesChanged = () => _trayController?.RefreshHostLabels(),
            HotkeysChanged = () => _hotkeys?.Apply(),
            TopologyChanged = () => { /* live toggle TBD — settings on disk, restart picks up */ },
        };
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            MacActivationPolicy.HideDockIcon();
        };
        _settingsWindow.Show();
    }

    private Dialogs.AboutDialog? _aboutDialog;
    private async System.Threading.Tasks.Task CheckForUpdatesNowAsync()
    {
        if (_updates is null) return;
        try
        {
            var info = await _updates.CheckAsync();
            var header = info is null ? "No updates available" : "Update available";
            var body = info is null
                ? $"You're up to date on {_updates.CurrentVersion}."
                : $"Update {info.Version} is available.\n\n{info.DownloadUrl}";
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                MacActivationPolicy.ShowDockIcon();
                var alert = new Dialogs.AlertWindow(header, body);
                alert.Closed += (_, _) =>
                {
                    if (_settingsWindow is null && _aboutDialog is null) MacActivationPolicy.HideDockIcon();
                };
                alert.Show();
            });
        }
        catch
        {
            // best effort — UpdateService stub never throws today
        }
    }

    private void ShowAbout()
    {
        if (_aboutDialog is not null && _aboutDialog.IsVisible)
        {
            _aboutDialog.Activate();
            return;
        }
        MacActivationPolicy.ShowDockIcon();
        _aboutDialog = new Dialogs.AboutDialog();
        _aboutDialog.Closed += (_, _) =>
        {
            _aboutDialog = null;
            // Only hide the dock icon if no other window kept it visible.
            if (_settingsWindow is null) MacActivationPolicy.HideDockIcon();
        };
        _aboutDialog.Show();
    }

    private bool _multiReceiverPromptShown;
    private void ShowMultiReceiverPrompt()
    {
        if (_multiReceiverPromptShown) return;
        if (_manager is null) return;

        _multiReceiverPromptShown = true;
        var prompt = new MultiReceiverPrompt(_manager, _policy!);
        prompt.Closed += (_, _) => _multiReceiverPromptShown = false;
        MacActivationPolicy.ShowDockIcon();
        prompt.Closed += (_, _) => MacActivationPolicy.HideDockIcon();
        prompt.Show();
    }
}
