using BoltMate.App.Services;
using BoltMate.Core.Services;
using System;
using System.IO;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using BoltMate.Core.Topology;
using BoltMate.Core.Permissions;
using Microsoft.Extensions.Logging;

namespace BoltMate.App.UI;

/// <summary>
/// Owns the tray icon image. Per June 2026 design handoff §3 the bolt
/// has only two states: <b>Default</b> (template silhouette — adapts
/// light/dark via OS chrome) and <b>Amber</b> (#FFBF00 attention badge)
/// — no green, no red. "Default = healthy" is the resting state.
///
/// Composite "needs attention" rule fires Amber when EITHER:
///   • a required OS permission is in the denied / alert state, OR
///   • any transport health is Blocked ≥ 30 seconds (the threshold
///     avoids a startup flash where a transport is briefly Blocked
///     before settling).
/// </summary>
/// <remarks>
/// macOS detail: template-image behavior (<c>NSImage.isTemplate</c>)
/// only works when the icon is a monochrome silhouette. Amber state
/// composites a colored badge over the silhouette and drops the
/// template flag so AppKit doesn't try to template-invert it.
/// </remarks>
public sealed class TrayIconStatusController : IDisposable
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckCadence = TimeSpan.FromSeconds(3);

    private readonly TrayIcon _trayIcon;
    private readonly ILogger _logger;
    private readonly CompositeDisposable _disposables = new();
    private readonly DispatcherTimer _timer;

    private readonly IPermissionsService _permissions;
    private IUdpTopologyService? _topology;
    private State _last = State.None;
    private OverallStatus _permissionStatus = OverallStatus.AllGood;

    private DateTimeOffset? _udpBlockedSince;
    private DateTimeOffset? _syncBlockedSince;
    private bool _udpBlockedAlert;
    private bool _syncBlockedAlert;

    private readonly SerialDisposable _udpHealthSub = new();
    private readonly SerialDisposable _syncHealthSub = new();

    // Cached compositions. Built lazily; rebuilt only on theme change.
    private WindowIcon? _neutralIcon;
    private WindowIcon? _alertIcon;
    // Tracks the URI the cached _neutralIcon was built from. When the
    // OS taskbar theme flips and the resolved URI no longer matches,
    // we discard the cache and rebuild. Win 11's `SystemUsesLightTheme`
    // does NOT trigger PlatformSettings.ColorValuesChanged (Avalonia
    // watches `AppsUseLightTheme`), so the poll is what catches it.
    private Uri? _neutralIconUri;

    public TrayIconStatusController(TrayIcon trayIcon, IPermissionsService permissions, ILogger logger)
    {
        _trayIcon = trayIcon;
        _permissions = permissions;
        _logger = logger;

        _disposables.Add(_udpHealthSub);
        _disposables.Add(_syncHealthSub);

        _timer = new DispatcherTimer(CheckCadence, DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
        _disposables.Add(Disposable.Create(_timer.Stop));

        // Theme change invalidates the cached compositions.
        var settings = Application.Current?.PlatformSettings;
        if (settings is not null)
        {
            settings.ColorValuesChanged += OnColorValuesChanged;
            _disposables.Add(Disposable.Create(() => settings.ColorValuesChanged -= OnColorValuesChanged));
        }

        Refresh();
    }

    private void OnColorValuesChanged(object? sender, PlatformColorValues e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _neutralIcon = _alertIcon = null;
            _last = State.None;
            Refresh();
        });
    }

    /// <summary>Attach the topology service so we can read peer state. Call after topology starts.</summary>
    public void Bind(IUdpTopologyService? topology)
    {
        _topology = topology;
        Refresh();
    }

    /// <summary>
    /// Push a fresh permission snapshot. The Alert state overrides peer
    /// health when any permission is denied. Call this every time the
    /// PermissionStatusService observable ticks.
    /// </summary>
    public void SetPermissionStatus(OverallStatus status)
    {
        if (_permissionStatus == status) return;
        _permissionStatus = status;
        Refresh();
    }

    public void BindHealth(IObservable<TransportHealth>? udpHealth, IObservable<TransportHealth>? syncHealth)
    {
        if (udpHealth is not null)
        {
            _udpHealthSub.Disposable = udpHealth.Subscribe(h => Dispatcher.UIThread.Post(() => OnUdpHealthChanged(h)));
        }
        else
        {
            _udpHealthSub.Disposable = Disposable.Empty;
            _udpBlockedSince = null;
            _udpBlockedAlert = false;
        }

        if (syncHealth is not null)
        {
            _syncHealthSub.Disposable = syncHealth.Subscribe(h => Dispatcher.UIThread.Post(() => OnSyncHealthChanged(h)));
        }
        else
        {
            _syncHealthSub.Disposable = Disposable.Empty;
            _syncBlockedSince = null;
            _syncBlockedAlert = false;
        }
    }

    private void OnUdpHealthChanged(TransportHealth h)
    {
        if (h.State is TransportState.Blocked)
        {
            if (_udpBlockedSince is null)
                _udpBlockedSince = DateTimeOffset.UtcNow;
        }
        else
        {
            _udpBlockedSince = null;
            _udpBlockedAlert = false;
        }
        Refresh();
    }

    private void OnSyncHealthChanged(TransportHealth h)
    {
        if (h.State is TransportState.Blocked)
        {
            if (_syncBlockedSince is null)
                _syncBlockedSince = DateTimeOffset.UtcNow;
        }
        else
        {
            _syncBlockedSince = null;
            _syncBlockedAlert = false;
        }
        Refresh();
    }

    private void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        if (_udpBlockedSince is not null && (now - _udpBlockedSince.Value) >= TimeSpan.FromSeconds(30))
            _udpBlockedAlert = true;
        else if (_udpBlockedSince is null)
            _udpBlockedAlert = false;

        if (_syncBlockedSince is not null && (now - _syncBlockedSince.Value) >= TimeSpan.FromSeconds(30))
            _syncBlockedAlert = true;
        else if (_syncBlockedSince is null)
            _syncBlockedAlert = false;

        var state = ResolveState();

        // Composite-cause tooltip per handoff: "BoltMate — action needed;
        // UDP, TCP transports blocked".
        string tooltip;
        if (state == State.Alert)
        {
            var causes = new System.Collections.Generic.List<string>(3);
            if (AreRequiredPermissionsDenied()) causes.Add("permissions");
            if (_udpBlockedAlert) causes.Add("UDP");
            if (_syncBlockedAlert) causes.Add("Bonjour/TCP");
            tooltip = causes.Count > 0
                ? $"BoltMate — action needed ({string.Join(", ", causes)} blocked)"
                : "BoltMate — action needed";
        }
        else
        {
            tooltip = "BoltMate";
        }

        // Detect taskbar theme flips on each poll — the cached
        // _neutralIcon needs to be discarded when SystemUsesLightTheme
        // changes (Win 11 settings → Personalization → Colors).
        var baseUri = ResolveBaseIconUri();
        var themeChanged = !Equals(baseUri, _neutralIconUri);
        if (themeChanged)
        {
            _neutralIcon = null;
            _neutralIconUri = baseUri;
        }

        try
        {
            if (state != _last || (themeChanged && state == State.Neutral))
            {
                _trayIcon.Icon = GetOrBuild(state);
                _last = state;
                // Template flag only valid in neutral state — any composited
                // badge (good / bad / alert) drops the template flag so AppKit
                // doesn't try to template-invert the colored sticker.
                try
                {
                    if (OperatingSystem.IsMacOS())
                    {
                        Avalonia.Controls.MacOSProperties.SetIsTemplateIcon(_trayIcon, state == State.Neutral);
                    }
                }
                catch { /* avalonia version diff — best effort */ }
            }

            _trayIcon.ToolTipText = tooltip;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tray icon refresh failed");
        }
    }

    private State ResolveState()
    {
        // Two-state model only: anything in the composite alert rule
        // returns Alert; everything else is the resting Neutral state.
        // Peer-presence has no UI signal at the bolt level anymore —
        // peers live in the Status tab's Other-computers card.
        if (AreRequiredPermissionsDenied() || _udpBlockedAlert || _syncBlockedAlert)
            return State.Alert;
        return State.Neutral;
    }

    private bool AreRequiredPermissionsDenied()
    {
        return !_permissions.Network.IsGranted || !_permissions.InputMonitoring.IsGranted;
    }

    private WindowIcon GetOrBuild(State state) => state switch
    {
        State.Alert => _alertIcon   ??= BuildAlert(),
        _           => _neutralIcon ??= BuildNeutral(),
    };

    private WindowIcon BuildNeutral()
    {
        // Pick the asset matching current OS theme. On Mac, AppKit will
        // template-invert; the asset choice doesn't matter much there.
        var uri = ResolveBaseIconUri();
        using var stream = AssetLoader.Open(uri);
        return new WindowIcon(stream);
    }

    private WindowIcon BuildAlert()
    {
        // Pre-rendered amber-tinted bolt — full silhouette in #FFBF00,
        // not a corner badge. Template flag is dropped at the call site
        // so AppKit shows the color as-is.
        using var stream = AssetLoader.Open(new Uri("avares://BoltMate/Assets/tray-icon-alert.png"));
        return new WindowIcon(stream);
    }

    /// <summary>
    /// Public form of <see cref="ResolveBaseIconUri"/> so the bootstrap
    /// can paint a theme-aware tray icon BEFORE this controller exists
    /// (during first-run welcome the controller hasn't been instantiated
    /// yet — without this, the XAML-default light asset stays on
    /// regardless of the OS theme).
    /// </summary>
    public static Uri ResolveNeutralIconUri() => ResolveBaseIconUri();

    /// <summary>Convenience: opens the avares stream and wraps in
    /// <see cref="WindowIcon"/>.</summary>
    public static WindowIcon LoadNeutralIcon()
    {
        using var stream = AssetLoader.Open(ResolveBaseIconUri());
        return new WindowIcon(stream);
    }

    private static Uri ResolveBaseIconUri()
    {
        // Mac: AppKit template-inverts the silhouette per menubar mode.
        // The "dark" asset (white bolt) is what we hand it.
        if (OperatingSystem.IsMacOS())
            return new Uri("avares://BoltMate/Assets/tray-icon-dark.png");

        // Windows: the system tray follows `SystemUsesLightTheme`, NOT
        // `AppsUseLightTheme`. Avalonia's PlatformSettings.GetColorValues
        // reads AppsUseLightTheme, which can be inverted from the system
        // setting (Win 11 exposes both as separate toggles). Read the
        // system value directly so the tray icon stays readable against
        // whatever the taskbar actually is.
        if (OperatingSystem.IsWindows())
        {
            return ReadWinSystemUsesLightTheme()
                ? new Uri("avares://BoltMate/Assets/tray-icon-light.png")
                : new Uri("avares://BoltMate/Assets/tray-icon-dark.png");
        }

        var theme = Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant;
        return theme == PlatformThemeVariant.Dark
            ? new Uri("avares://BoltMate/Assets/tray-icon-dark.png")
            : new Uri("avares://BoltMate/Assets/tray-icon-light.png");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool ReadWinSystemUsesLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("SystemUsesLightTheme") is int v)
                return v != 0;
        }
        catch { /* registry read failed — fall through to light default */ }
        return true;
    }

    public void Dispose() => _disposables.Dispose();

    private enum State { None, Neutral, Alert }
}
