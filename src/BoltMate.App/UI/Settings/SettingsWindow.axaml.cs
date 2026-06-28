using BoltMate.Core.Services;
using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
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
/// Settings shell: Status / General / License (left-rail nav). All
/// state + behaviour lives in <see cref="SettingsViewModel"/>; the
/// code-behind here only does Avalonia-specific UI plumbing — XAML
/// init, window lifecycle, and the App-layer-facing OpenTo entry
/// point that swings the VM's CurrentTab.
/// </summary>
public partial class SettingsWindow : ReactiveWindow<SettingsViewModel>
{
    // Re-exposed so App.axaml.cs and TrayMenuController can refer to
    // the canonical tab keys without taking a dependency on the VM
    // type directly. Values intentionally match SettingsViewModel's
    // TabXxx constants character-for-character.
    public const string TabStatus  = "status";
    public const string TabGeneral = "general";
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
    }

    private BoltMate.App.Services.Support.SupportSubmissionService? _support;
    private BoltMate.Licensing.ILicenseGate? _licenseGate;

    public SettingsWindow(
        IReceiverManager manager,
        AppSettings settings,
        IPermissionsService permissions,
        BoltMate.App.Core.Notifications.INotificationService? notifications = null,
        IObservable<TransportHealth>? udpHealth = null,
        IObservable<TransportHealth>? syncHealth = null,
        BoltMate.App.Services.Support.SupportSubmissionService? support = null,
        BoltMate.Licensing.ILicenseGate? licenseGate = null) : this()
    {
        _support = support;
        _licenseGate = licenseGate;
        ViewModel = new SettingsViewModel(
            manager,
            settings,
            permissions,
            new UpdateService(settings, NullLoggerFactory.Instance.CreateLogger<UpdateService>()),
            notifications,
            udpHealth,
            syncHealth,
            peerAnnouncementsProvider: () => PeerAnnouncementsProvider?.Invoke() ?? Array.Empty<ReceiverAnnouncement>(),
            peerStatsProvider: () => PeerStatsProvider?.Invoke() ?? Array.Empty<PeerStats>(),
            support: support,
            licenseGate: licenseGate);
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
            ViewModel.Notifications,
            udpHealth,
            syncHealth,
            peerAnnouncementsProvider: () => PeerAnnouncementsProvider?.Invoke() ?? Array.Empty<ReceiverAnnouncement>(),
            peerStatsProvider: () => PeerStatsProvider?.Invoke() ?? Array.Empty<PeerStats>(),
            support: _support,
            licenseGate: _licenseGate);
        if (IsVisible) ViewModel.WireActivation();
    }

    /// <summary>
    /// Selects which tab is active. Routed through the VM so the bound
    /// nav buttons + page visibility flip together. Called by the tray
    /// menu and the App layer's initial-tab handoff.
    /// </summary>
    public void OpenTo(string tab)
    {
        if (ViewModel is null) return;
        ViewModel.CurrentTab = tab;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
