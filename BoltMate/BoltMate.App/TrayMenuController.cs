using System;
using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using BoltMate.Core.Bolt;
using BoltMate.Core.Topology;
using Microsoft.Extensions.Logging;

namespace BoltMate.App;

/// <summary>
/// Minimal tray menu mirroring the three Settings tabs: Status, About,
/// License, plus Quit. Connection health is conveyed by the tray-icon badge,
/// not by menu text — so the menu labels are static.
/// </summary>
public sealed class TrayMenuController : IDisposable
{
    private readonly NativeMenu _menu;
    private readonly ReceiverManager _manager;
    private readonly ILogger<TrayMenuController> _logger;
    private readonly CompositeDisposable _disposables = new();

    public TrayMenuController(
        NativeMenu menu,
        ReceiverManager manager,
        ILogger<TrayMenuController> logger)
    {
        _menu = menu;
        _manager = manager;
        _logger = logger;

        BuildItems();
    }

    public Action? OnStatusClicked { get; set; }
    public Action? OnAboutClicked { get; set; }
    public Action? OnLicenseClicked { get; set; }

    /// <summary>Bind the topology service. Tray-icon badge owns connection state; nothing to do here.</summary>
    public void Bind(UdpTopologyService? topology) { }

    /// <summary>Stub for legacy callers — host-label customisation was cut.</summary>
    public void RefreshHostLabels() { }

    public void Dispose() => _disposables.Dispose();

    private void BuildItems()
    {
        _menu.Items.Clear();

        var statusItem = new NativeMenuItem("Status");
        statusItem.Click += (_, _) => OnStatusClicked?.Invoke();

        var aboutItem = new NativeMenuItem("About");
        aboutItem.Click += (_, _) => OnAboutClicked?.Invoke();

        var licenseItem = new NativeMenuItem("License");
        licenseItem.Click += (_, _) => OnLicenseClicked?.Invoke();

        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        };

        _menu.Items.Add(statusItem);
        _menu.Items.Add(aboutItem);
        _menu.Items.Add(licenseItem);
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(quitItem);
    }
}
