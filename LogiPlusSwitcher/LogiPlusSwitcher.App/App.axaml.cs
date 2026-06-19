using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DynamicData;
using LogiPlusSwitcher.App.Licensing;
using Avalonia.Threading;
using LogiPlusSwitcher.Core;
using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Core.Hid;
using LogiPlusSwitcher.Core.Switcher;
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
        HidApiBridge.EnsureNativeLibraryResolver();
        HidApiBridge.SetMacOsNonExclusive();

        var transport = new HidApiReceiverTransport(_loggerFactory);
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

        // Auto-enrich devices on link-up (feature discovery, name, battery, host bindings).
        _disposables.Add(new DeviceEnricher(_manager, _loggerFactory.CreateLogger<DeviceEnricher>()));

        // Wire the dynamic tray menu now that the manager is up.
        var trays = TrayIcon.GetIcons(this);
        if (trays is { Count: > 0 } && trays[0].Menu is { } menu)
        {
            _trayController = new TrayMenuController(menu, _manager, _license,
                _loggerFactory.CreateLogger<TrayMenuController>())
            {
                OnSettingsClicked = OpenSettings,
                OnAboutClicked = ShowAbout,
            };
            _disposables.Add(_trayController);
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

        _settingsWindow = new SettingsWindow(_manager, _policy, _license, _settings);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            MacActivationPolicy.HideDockIcon();
        };
        _settingsWindow.Show();
    }

    private void ShowAbout()
    {
        // TODO: real about dialog.
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
