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
using Microsoft.Extensions.Logging;

namespace BoltMate.App;

/// <summary>
/// Owns the tray icon image and updates it based on cross-machine peer
/// connection health AND OS-permission state. States, in render priority
/// (highest first):
///   • Alert   — at least one OS permission denied. Yellow "!" badge.
///   • Bad     — topology on with peers known, but none seen within window. Red X badge.
///   • Good    — at least one peer seen within <see cref="StaleAfter"/>. Green check badge.
///   • Neutral — topology off OR no peers ever discovered. Plain template icon.
/// </summary>
/// <remarks>
/// macOS detail: template-image behavior (<c>NSImage.isTemplate</c>) only
/// works when the icon is a monochrome silhouette. The composite states
/// (good/bad) bake a colored badge over the silhouette, so we drop the
/// template flag for those — AppKit will not auto-invert. We use a white
/// silhouette which reads on the typical dark menubar. Acceptable trade-off
/// to surface connection state at a glance.
/// </remarks>
public sealed class TrayIconStatusController : IDisposable
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckCadence = TimeSpan.FromSeconds(3);

    private readonly TrayIcon _trayIcon;
    private readonly ILogger _logger;
    private readonly CompositeDisposable _disposables = new();
    private readonly DispatcherTimer _timer;

    private UdpTopologyService? _topology;
    private State _last = State.None;
    private OverallStatus _permissionStatus = OverallStatus.AllGood;

    // Cached compositions. Built lazily; rebuilt only on theme change.
    private WindowIcon? _neutralIcon;
    private WindowIcon? _goodIcon;
    private WindowIcon? _badIcon;
    private WindowIcon? _alertIcon;

    public TrayIconStatusController(TrayIcon trayIcon, ILogger logger)
    {
        _trayIcon = trayIcon;
        _logger = logger;

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

    /// <summary>Attach the topology service so we can read peer state. Call after topology starts.</summary>
    public void Bind(UdpTopologyService? topology)
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

    private void OnColorValuesChanged(object? sender, PlatformColorValues e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _neutralIcon = _goodIcon = _badIcon = _alertIcon = null;
            _last = State.None;
            Refresh();
        });
    }

    private void Refresh()
    {
        var state = ResolveState();
        if (state == _last) return;
        try
        {
            _trayIcon.Icon = GetOrBuild(state);
            _trayIcon.ToolTipText = state == State.Alert
                ? "BoltMate — permissions needed"
                : "BoltMate";
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tray icon refresh failed");
        }
    }

    private State ResolveState()
    {
        // Alert beats every other state — a denied permission is the most
        // important thing for the user to know about.
        if (_permissionStatus == OverallStatus.AnyDenied)
            return State.Alert;

        if (_topology is null) return State.Neutral;
        var peers = _topology.PeerSnapshot;
        if (peers.Count == 0) return State.Neutral;
        var cutoff = DateTime.UtcNow - StaleAfter;
        foreach (var p in peers)
            if (p.LastSeenUtc >= cutoff)
                return State.Good;
        return State.Bad;
    }

    private WindowIcon GetOrBuild(State state) => state switch
    {
        State.Alert   => _alertIcon   ??= Build(BadgeKind.Alert),
        State.Good    => _goodIcon    ??= Build(BadgeKind.Good),
        State.Bad     => _badIcon     ??= Build(BadgeKind.Bad),
        _             => _neutralIcon ??= BuildNeutral(),
    };

    private WindowIcon BuildNeutral()
    {
        // Pick the asset matching current OS theme. On Mac, AppKit will
        // template-invert; the asset choice doesn't matter much there.
        var uri = ResolveBaseIconUri();
        using var stream = AssetLoader.Open(uri);
        return new WindowIcon(stream);
    }

    private WindowIcon Build(BadgeKind kind)
    {
        var uri = ResolveBaseIconUri();
        using var baseStream = AssetLoader.Open(uri);
        using var baseBitmap = new Bitmap(baseStream);

        var w = baseBitmap.PixelSize.Width;
        var h = baseBitmap.PixelSize.Height;
        var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.DrawImage(baseBitmap, new Rect(0, 0, w, h));

            // Badge sits in the lower-right corner ~40% of the icon dimension.
            var badgeSize = Math.Max(10, w * 0.45);
            var rect = new Rect(w - badgeSize, h - badgeSize, badgeSize, badgeSize);
            var fill = kind switch
            {
                BadgeKind.Good  => Brushes.LimeGreen,
                BadgeKind.Bad   => Brushes.OrangeRed,
                BadgeKind.Alert => Brushes.Gold,
                _               => Brushes.Gray,
            };
            ctx.DrawEllipse(fill, new Pen(Brushes.White, badgeSize * 0.08), rect.Center, rect.Width / 2, rect.Height / 2);

            var glyphPen = new Pen(Brushes.White, badgeSize * 0.14, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            if (kind == BadgeKind.Good)
            {
                // Check: two strokes — short down-right then longer up-right.
                var p1 = new Point(rect.X + badgeSize * 0.25, rect.Y + badgeSize * 0.55);
                var p2 = new Point(rect.X + badgeSize * 0.45, rect.Y + badgeSize * 0.72);
                var p3 = new Point(rect.X + badgeSize * 0.78, rect.Y + badgeSize * 0.32);
                ctx.DrawLine(glyphPen, p1, p2);
                ctx.DrawLine(glyphPen, p2, p3);
            }
            else if (kind == BadgeKind.Bad)
            {
                // X: two diagonals.
                var pad = badgeSize * 0.28;
                ctx.DrawLine(glyphPen, new Point(rect.X + pad, rect.Y + pad), new Point(rect.Right - pad, rect.Bottom - pad));
                ctx.DrawLine(glyphPen, new Point(rect.Right - pad, rect.Y + pad), new Point(rect.X + pad, rect.Bottom - pad));
            }
            else // Alert: exclamation point — vertical stroke + dot.
            {
                var cx = rect.X + badgeSize * 0.5;
                var topY = rect.Y + badgeSize * 0.25;
                var stemBottomY = rect.Y + badgeSize * 0.6;
                ctx.DrawLine(glyphPen, new Point(cx, topY), new Point(cx, stemBottomY));
                var dotRadius = badgeSize * 0.09;
                ctx.DrawEllipse(Brushes.White, null, new Point(cx, rect.Y + badgeSize * 0.78), dotRadius, dotRadius);
            }
        }

        using var ms = new MemoryStream();
        rtb.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }

    private static Uri ResolveBaseIconUri()
    {
        // On Windows, pick light/dark based on platform theme so the
        // silhouette is readable. On Mac with the template flag dropped we
        // use the dark variant — a white silhouette renders on the typical
        // dark menubar.
        if (OperatingSystem.IsMacOS())
            return new Uri("avares://BoltMate/Assets/tray-icon-dark.png");

        var theme = Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant;
        return theme == PlatformThemeVariant.Dark
            ? new Uri("avares://BoltMate/Assets/tray-icon-dark.png")
            : new Uri("avares://BoltMate/Assets/tray-icon-light.png");
    }

    public void Dispose() => _disposables.Dispose();

    private enum State { None, Neutral, Good, Bad, Alert }
    private enum BadgeKind { Good, Bad, Alert }
}
