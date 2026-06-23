using BoltMate.Core.Services;
using System;
using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using BoltMate.Core.Bolt;
using BoltMate.Core.Topology;
using BoltMate.Core.Permissions;
using Microsoft.Extensions.Logging;

namespace BoltMate.App;

/// <summary>
/// Tray menu mirroring the Settings tabs: Status, About, License, plus Quit.
/// Connection health is conveyed by the tray-icon badge — menu labels are
/// static EXCEPT for a top "Fix permissions…" item that appears when the
/// permission service reports a denied permission. Clicking it opens the
/// WelcomeWindow at the first un-granted primer page.
/// </summary>
public sealed class TrayMenuController : IDisposable
{
    private readonly NativeMenu _menu;
    private readonly IReceiverManager _manager;
    private readonly IPermissionsService _permissions;
    private readonly ILogger<TrayMenuController> _logger;
    private readonly CompositeDisposable _disposables = new();

    // When non-null, the rebuilt menu pins a "Fix permissions…" item to
    // the very top. The App layer toggles this via SetPermissionStatus.
    private OverallStatus _permissionStatus = OverallStatus.AllGood;

    public TrayMenuController(
        NativeMenu menu,
        IReceiverManager manager,
        IPermissionsService permissions,
        ILogger<TrayMenuController> logger)
    {
        _menu = menu;
        _manager = manager;
        _permissions = permissions;
        _logger = logger;

        BuildItems();
    }

    public Action? OnStatusClicked { get; set; }
    public Action? OnAboutClicked { get; set; }
    public Action? OnLicenseClicked { get; set; }

    /// <summary>Click handler for the alert-state "Fix permissions…" entry.</summary>
    public Action? OnFixPermissionsClicked { get; set; }

    /// <summary>Bind the topology service. Tray-icon badge owns connection state; nothing to do here.</summary>
    public void Bind(IUdpTopologyService? topology) { }

    /// <summary>Stub for legacy callers — host-label customisation was cut.</summary>
    public void RefreshHostLabels() { }

    /// <summary>Push a fresh permission snapshot; rebuilds the menu only if alert-presence flipped.</summary>
    public void SetPermissionStatus(OverallStatus status)
    {
        var wasAlerting = _permissionStatus is OverallStatus.AnyDenied;
        var nowAlerting = status is OverallStatus.AnyDenied;
        _permissionStatus = status;
        if (wasAlerting == nowAlerting) return;
        Dispatcher.UIThread.Post(BuildItems);
    }

    public void Dispose() => _disposables.Dispose();

    private void BuildItems()
    {
        _menu.Items.Clear();

        var permissionsDenied = !_permissions.Network.IsGranted || !_permissions.InputMonitoring.IsGranted;
        if (permissionsDenied)
        {
            var fixItem = new NativeMenuItem("⚠ Fix permissions…");
            fixItem.Click += (_, _) => OnFixPermissionsClicked?.Invoke();
            _menu.Items.Add(fixItem);
            _menu.Items.Add(new NativeMenuItemSeparator());
        }

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
