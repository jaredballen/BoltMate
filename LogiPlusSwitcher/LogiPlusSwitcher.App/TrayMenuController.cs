using System;
using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Core.Topology;
using Microsoft.Extensions.Logging;

namespace LogiPlusSwitcher.App;

/// <summary>
/// Minimal tray menu: a peer-status header that opens the Status view, plus
/// Settings, About, Quit. No per-receiver or per-device entries — those live
/// in the Settings → Status tab.
/// </summary>
public sealed class TrayMenuController : IDisposable
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

    private readonly NativeMenu _menu;
    private readonly ReceiverManager _manager;
    private readonly ILogger<TrayMenuController> _logger;
    private readonly CompositeDisposable _disposables = new();
    private readonly DispatcherTimer _refreshTimer;

    private NativeMenuItem _statusItem = null!;
    private UdpTopologyService? _topology;

    public TrayMenuController(
        NativeMenu menu,
        ReceiverManager manager,
        ILogger<TrayMenuController> logger)
    {
        _menu = menu;
        _manager = manager;
        _logger = logger;

        BuildItems();
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => RefreshStatus());
        _refreshTimer.Start();
        _disposables.Add(Disposable.Create(_refreshTimer.Stop));
    }

    public Action? OnStatusClicked { get; set; }
    public Action? OnSettingsClicked { get; set; }
    public Action? OnAboutClicked { get; set; }

    /// <summary>Bind the topology service so status text reflects peer health.</summary>
    public void Bind(UdpTopologyService? topology)
    {
        _topology = topology;
        RefreshStatus();
    }

    /// <summary>Stub for legacy callers — host-label customisation was cut.</summary>
    public void RefreshHostLabels() { }

    public void Dispose() => _disposables.Dispose();

    private void BuildItems()
    {
        _menu.Items.Clear();

        _statusItem = new NativeMenuItem("Status");
        _statusItem.Click += (_, _) => OnStatusClicked?.Invoke();

        var settingsItem = new NativeMenuItem("Settings…");
        settingsItem.Click += (_, _) => OnSettingsClicked?.Invoke();

        var aboutItem = new NativeMenuItem("About LogiPlusSwitcher");
        aboutItem.Click += (_, _) => OnAboutClicked?.Invoke();

        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        };

        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(aboutItem);
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(quitItem);

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_topology is null)
        {
            _statusItem.Header = "Status: topology off";
            return;
        }
        var peers = _topology.PeerSnapshot;
        if (peers.Count == 0)
        {
            _statusItem.Header = "Status: no peers";
            return;
        }
        var cutoff = DateTime.UtcNow - StaleAfter;
        var active = 0;
        foreach (var p in peers)
            if (p.LastSeenUtc >= cutoff) active++;
        _statusItem.Header = active == peers.Count
            ? $"Status: connected to {active} peer{(active == 1 ? "" : "s")}"
            : $"Status: {active}/{peers.Count} peer{(peers.Count == 1 ? "" : "s")} active";
    }
}
