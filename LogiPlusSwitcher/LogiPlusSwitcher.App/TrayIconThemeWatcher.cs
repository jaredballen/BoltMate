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
/// Swaps the menubar / system-tray icon between a black silhouette
/// (light OS theme) and a white silhouette (dark OS theme) so the icon
/// remains visible across themes.
/// </summary>
/// <remarks>
/// macOS handles this natively via NSImage template mode, but Avalonia 12
/// doesn't expose that flag. Doing the swap ourselves works on Mac AND
/// Windows: macOS's <see cref="IPlatformSettings.ColorValuesChanged"/> fires
/// when the user toggles Appearance, and Windows fires the same event when
/// the system theme changes too.
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
            // Dark theme → white icon (light pixels visible on dark menubar)
            // Light theme → black icon
            var uri = theme == PlatformThemeVariant.Dark ? DarkIconUri : LightIconUri;
            using var stream = AssetLoader.Open(uri);
            _trayIcon.Icon = new WindowIcon(stream);
            _logger.LogInformation("Tray icon refreshed for {Theme} theme", theme);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh tray icon for theme change");
        }
    }

    public void Dispose() => _disposables.Dispose();
}
