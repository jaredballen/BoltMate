using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DynamicData;
using LogiPlusSwitcher.App.Licensing;
using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Core.Hid;
using LogiPlusSwitcher.Core.Switcher;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogiPlusSwitcher.App;

public partial class App : Application
{
    private readonly CompositeDisposable _disposables = new();
    private readonly System.Collections.Generic.Dictionary<string, SwitcherService> _switchers = new();
    private ReceiverManager? _manager;
    private TrayMenuController? _trayController;
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

        HidApiBridge.EnsureNativeLibraryResolver();
        HidApiBridge.SetMacOsNonExclusive();

        var transport = new HidApiReceiverTransport(_loggerFactory);
        _manager = new ReceiverManager(transport, loggerFactory: _loggerFactory);

        _disposables.Add(_manager);
        _disposables.Add(_loggerFactory);

        // Spin up a per-receiver SwitcherService so attach + fan-out works
        // even before the user opens the tray menu.
        _disposables.Add(_manager.Receivers.Connect().Subscribe(changes =>
        {
            foreach (var change in changes)
            {
                if (change.Reason == ChangeReason.Add)
                {
                    var switcher = new SwitcherService(change.Current, _loggerFactory.CreateLogger<SwitcherService>());
                    _switchers[change.Key] = switcher;
                }
                else if (change.Reason == ChangeReason.Remove)
                {
                    if (_switchers.Remove(change.Key, out var sw))
                        sw.Dispose();
                }
            }
        }));

        // Auto-enrich devices on link-up (feature discovery, name, battery).
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
        if (_manager is null) return;

        if (_settingsWindow is not null && _settingsWindow.IsVisible)
        {
            _settingsWindow.Activate();
            return;
        }

        // Show the dock icon for the duration of the settings window so users
        // who Cmd-Tab can find us. Restore accessory mode on close.
        MacActivationPolicy.ShowDockIcon();

        _settingsWindow = new SettingsWindow(_manager);
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
}
