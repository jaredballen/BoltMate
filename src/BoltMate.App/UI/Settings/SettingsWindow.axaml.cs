using BoltMate.Core.Services;
using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using FluentAvalonia.UI.Controls;
using BoltMate.App.Services;
using BoltMate.App.UI;
using BoltMate.Core;
using BoltMate.Core.Bolt;
using BoltMate.Core.Permissions;
using BoltMate.Core.Topology;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.App.UI;

/// <summary>
/// Settings shell: Status / About / License (side tabs via FluentAvalonia
/// NavigationView). All state + behaviour lives in
/// <see cref="SettingsViewModel"/>; the code-behind here only does
/// Avalonia-specific UI plumbing — XAML init, page swap, lifecycle.
/// </summary>
public partial class SettingsWindow : ReactiveWindow<SettingsViewModel>
{
    public const string TabStatus = "status";
    public const string TabAbout = "about";
    public const string TabLicense = "license";

    public SettingsWindow()
    {
        InitializeComponent();

        // Pre-warm: App constructs this once at startup and keeps it alive
        // across open/close cycles. User-clicked close must NOT actually
        // destroy the window — cancel the close and hide instead. Real
        // disposal happens on App teardown via Hibernate().
        Closing += (_, e) =>
        {
            if (_hibernating) return;
            e.Cancel = true;
            Hide();
            OnHidden?.Invoke();
        };

        // ViewModel may be set later (DataContext-only ctor path is
        // designer/XAML; production uses the parameterised ctor below).
        // When the VM lands, wire its activation lifecycle to the window's
        // open/close. Per-activation subscriptions are torn down on close.
        Opened += (_, _) => ViewModel?.WireActivation();
        Closed += (_, _) => ViewModel?.TeardownActivation();

        // NavigationView selection drives page swap.
        var nav = this.FindControl<FANavigationView>("MainNav");
        if (nav is not null)
        {
            nav.SelectionChanged += OnNavSelectionChanged;
            var statusItem = this.FindControl<FANavigationViewItem>("StatusNavItem");
            if (statusItem is not null) nav.SelectedItem = statusItem;
        }
    }

    public SettingsWindow(
        IReceiverManager manager,
        AppSettings settings,
        IPermissionsService permissions,
        IObservable<TransportHealth>? udpHealth = null,
        IObservable<TransportHealth>? syncHealth = null) : this()
    {
        ViewModel = new SettingsViewModel(
            manager,
            settings,
            permissions,
            new UpdateService(settings, NullLoggerFactory.Instance.CreateLogger<UpdateService>()),
            udpHealth,
            syncHealth,
            peerAnnouncementsProvider: () => PeerAnnouncementsProvider?.Invoke() ?? Array.Empty<ReceiverAnnouncement>(),
            peerStatsProvider: () => PeerStatsProvider?.Invoke() ?? Array.Empty<PeerStats>());
    }

    private bool _hibernating;

    /// <summary>Tells App layer that the user dismissed the window (still alive, hidden).</summary>
    public Action? OnHidden { get; set; }

    /// <summary>Permanently closes the window. App calls this on shutdown.</summary>
    public void Hibernate()
    {
        _hibernating = true;
        Close();
    }

    /// <summary>Accessor returning the latest UDP announcement from each known peer.</summary>
    public Func<IEnumerable<ReceiverAnnouncement>>? PeerAnnouncementsProvider { get; set; }

    /// <summary>Accessor returning per-peer stats.</summary>
    public Func<IEnumerable<PeerStats>>? PeerStatsProvider { get; set; }

    /// <summary>Accessor returning send-side (attempts, errors). Unused after scope cut.</summary>
    public Func<(long Attempts, long Errors)>? SendStatsProvider { get; set; }

    /// <summary>Surface hook still wired to the App layer; no-op today.</summary>
    public Action? HostNamesChanged { get; set; }

    /// <summary>Hook for the App layer when network/topology toggles change. Unused after scope cut.</summary>
    public Action? TopologyChanged { get; set; }

    /// <summary>
    /// Re-wires transport-health observables. Today the App calls this once
    /// at construction; live re-wire happens by the App rebuilding the
    /// observable when topology toggles on/off. Calling here rebuilds the
    /// VM with the new sources.
    /// </summary>
    public void BindHealth(IObservable<TransportHealth>? udpHealth, IObservable<TransportHealth>? syncHealth)
    {
        if (ViewModel is null) return;

        // Tear down current activation, swap the underlying observables,
        // then reactivate. Cheap because the VM holds references, not deep
        // state.
        ViewModel.TeardownActivation();
        ViewModel = new SettingsViewModel(
            ViewModel.Manager,
            ViewModel.Settings,
            ViewModel.Permissions,
            ViewModel.Updates,
            udpHealth,
            syncHealth,
            peerAnnouncementsProvider: () => PeerAnnouncementsProvider?.Invoke() ?? Array.Empty<ReceiverAnnouncement>(),
            peerStatsProvider: () => PeerStatsProvider?.Invoke() ?? Array.Empty<PeerStats>());
        if (IsVisible) ViewModel.WireActivation();
    }

    /// <summary>Selects which page is active. Called by the tray menu.</summary>
    public void OpenTo(string tab)
    {
        var nav = this.FindControl<FANavigationView>("MainNav");
        if (nav is null) return;
        FANavigationViewItem? target = tab switch
        {
            TabAbout => this.FindControl<FANavigationViewItem>("AboutNavItem"),
            TabLicense => this.FindControl<FANavigationViewItem>("LicenseNavItem"),
            _ => this.FindControl<FANavigationViewItem>("StatusNavItem"),
        };
        if (target is not null) nav.SelectedItem = target;
        ShowPage(tab);
    }

    private void OnNavSelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
    {
        if (e.SelectedItem is not FANavigationViewItem nvi) return;
        ShowPage(nvi.Tag as string);
    }

    private void ShowPage(string? tag)
    {
        var statusPage = this.FindControl<Control>("StatusPage");
        var aboutPage = this.FindControl<Control>("AboutPage");
        var licensePage = this.FindControl<Control>("LicensePage");
        if (statusPage is not null) statusPage.IsVisible = tag == TabStatus;
        if (aboutPage is not null) aboutPage.IsVisible = tag == TabAbout;
        if (licensePage is not null) licensePage.IsVisible = tag == TabLicense;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
