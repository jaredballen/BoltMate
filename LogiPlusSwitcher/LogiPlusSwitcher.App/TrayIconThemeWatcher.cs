using System;
using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace LogiPlusSwitcher.App;

/// <summary>
/// Keeps the tray icon visible on both light and dark menubars.
/// </summary>
/// <remarks>
/// On macOS, Avalonia exposes <c>MacOSProperties.IsTemplateIcon</c> (set in
/// App.axaml) which maps to <c>NSImage.isTemplate = YES</c>; AppKit handles
/// the inversion natively, including selection vibrancy when the menu is
/// open. This watcher is a no-op there.
///
/// On Windows there's no OS-level template concept — tray icons are static
/// bitmaps. The watcher subscribes to <see cref="IPlatformSettings.ColorValuesChanged"/>
/// and swaps a black silhouette (light taskbar) for a white one (dark
/// taskbar). Same approach Microsoft's own monochrome tray apps take.
/// </remarks>
public sealed class TrayIconThemeWatcher : IDisposable
{
    private readonly TrayIcon _trayIcon;
    private readonly ILogger _logger;
    private readonly CompositeDisposable _disposables = new();

    private static readonly Uri LightIconUri = new("avares://LogiPlusSwitcher.App/Assets/tray-icon-light.png");
    private static readonly Uri DarkIconUri = new("avares://LogiPlusSwitcher.App/Assets/tray-icon-dark.png");

    public TrayIconThemeWatcher(TrayIcon trayIcon, ILogger logger)
    {
        _trayIcon = trayIcon;
        _logger = logger;

        if (OperatingSystem.IsMacOS()) return; // AppKit handles it via template image

        UpdateIcon();

        var settings = Application.Current?.PlatformSettings;
        if (settings is not null)
        {
            settings.ColorValuesChanged += OnColorValuesChanged;
            _disposables.Add(Disposable.Create(() => settings.ColorValuesChanged -= OnColorValuesChanged));
        }
    }

    private void OnColorValuesChanged(object? sender, PlatformColorValues e)
    {
        Dispatcher.UIThread.Post(UpdateIcon);
    }

    private void UpdateIcon()
    {
        try
        {
            var theme = Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant;
            var uri = theme == PlatformThemeVariant.Dark ? DarkIconUri : LightIconUri;
            using var stream = AssetLoader.Open(uri);
            _trayIcon.Icon = new WindowIcon(stream);
            _logger.LogInformation("Tray icon refreshed for {Theme} theme (Windows manual swap)", theme);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh tray icon for theme change");
        }
    }

    public void Dispose() => _disposables.Dispose();
}
