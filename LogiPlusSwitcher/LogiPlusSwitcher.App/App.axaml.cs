using System;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DynamicData;
using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Core.Hid;
using LogiPlusSwitcher.Core.Switcher;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogiPlusSwitcher.App;

public partial class App : Application
{
    private const int CountMenuItemIndex = 4; // index of "0 receivers..." in the menu

    private readonly CompositeDisposable _disposables = new();
    private readonly System.Collections.Generic.Dictionary<string, SwitcherService> _switchers = new();
    private ReceiverManager? _manager;
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Tray-first: no main window. Shut down only when the user picks Quit.
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
        _disposables.Add(_manager.Receivers.Connect()
            .Subscribe(changes =>
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
                Dispatcher.UIThread.Post(UpdateTrayCount);
            }));

        base.OnFrameworkInitializationCompleted();
    }

    private void UpdateTrayCount()
    {
        var receiverCount = _manager?.Receivers.Count ?? 0;
        var deviceCount = 0;
        if (_manager is not null)
        {
            foreach (var receiver in _manager.Receivers.Items)
                deviceCount += receiver.Devices.Count;
        }

        var trays = TrayIcon.GetIcons(this);
        if (trays is null || trays.Count == 0) return;
        var menu = trays[0].Menu;
        if (menu is null || menu.Items.Count <= CountMenuItemIndex) return;
        if (menu.Items[CountMenuItemIndex] is NativeMenuItem item)
        {
            item.Header = $"{receiverCount} receiver{(receiverCount == 1 ? "" : "s")} · {deviceCount} device{(deviceCount == 1 ? "" : "s")}";
        }
    }

    private void OnSwitchAllHost1(object? sender, EventArgs e) => SwitchAllTo(0);
    private void OnSwitchAllHost2(object? sender, EventArgs e) => SwitchAllTo(1);
    private void OnSwitchAllHost3(object? sender, EventArgs e) => SwitchAllTo(2);

    private void SwitchAllTo(byte targetHost)
    {
        if (_manager is null) return;
        foreach (var receiver in _manager.Receivers.Items)
        {
            foreach (var device in receiver.Devices.Items)
            {
                if (device.CanReceiveHostSwitch)
                    receiver.TrySwitchHost(device.DeviceIndex, targetHost);
            }
        }
    }

    private void OnQuit(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
