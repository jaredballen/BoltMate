using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BoltMate.Core;
using BoltMate.Hid.IOKit;

namespace BoltMate.App.Welcome;

/// <summary>
/// First-run welcome wizard and on-demand "fix permissions" entry point.
///
/// Pages (Mac):     Welcome → Network → Input Monitoring → Done
/// Pages (Windows): Welcome → Network → Done       (Input Monitoring skipped)
/// Pages (Linux):   Linux fast-path → close immediately
///
/// All pages live in the XAML as sibling Grids; only one is visible at a
/// time. Page index is the array position in <see cref="_pages"/>.
///
/// Modal-ish contract:
///   • Closing the window via [x] / Cmd-W counts as the user opting OUT
///     and quits the app (per the spec).
///   • Closing via the "Open BoltMate" / "Done" path is the happy exit:
///     flips <see cref="AppSettings.HasShownWelcome"/> true, saves, and
///     fires <see cref="WelcomeCompleted"/> so the App layer continues
///     startup + opens the Settings window to the Status tab.
/// </summary>
public partial class WelcomeWindow : Window
{
    public const string PermissionNetwork = "network";
    public const string PermissionInputMonitoring = "input-monitoring";

    // Wait time after triggering an OS prompt before we advance to the next
    // page. Gives the system roughly enough time to surface the dialog so
    // the next page doesn't visibly race with the prompt UI.
    private static readonly TimeSpan PromptSettleDelay = TimeSpan.FromMilliseconds(1500);

    private readonly AppSettings _settings;

    private Grid[] _pages = Array.Empty<Grid>();
    private int _currentPageIndex;

    // Flips to true on the happy path (Done button). The Closing handler
    // uses this to distinguish "user finished the wizard" from "user
    // dismissed the wizard" — the latter quits the app.
    private bool _completedSuccessfully;

    /// <summary>
    /// Fired exactly once on the happy path right before the window closes.
    /// The App layer subscribes to this to start the rest of bootstrap +
    /// open Settings.
    /// </summary>
    public event Action? WelcomeCompleted;

    /// <summary>
    /// Designer-only ctor. Real usage goes through the (AppSettings) ctor.
    /// </summary>
    public WelcomeWindow() : this(new AppSettings()) { }

    public WelcomeWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        // Build the per-platform page list. Linux gets the fast-path only.
        var welcome = this.FindControl<Grid>("PageWelcome")!;
        var network = this.FindControl<Grid>("PageNetwork")!;
        var inputMonitoring = this.FindControl<Grid>("PageInputMonitoring")!;
        var done = this.FindControl<Grid>("PageDone")!;
        var linux = this.FindControl<Grid>("PageLinux")!;

        if (OperatingSystem.IsLinux())
        {
            _pages = new[] { linux };
        }
        else if (OperatingSystem.IsWindows())
        {
            _pages = new[] { welcome, network, done };
        }
        else
        {
            // mac (default for any other OperatingSystem combo)
            _pages = new[] { welcome, network, inputMonitoring, done };
        }

        ShowPage(0);

        // Intercept manual close: anything that doesn't go through the
        // happy-path Done button is treated as a hard opt-out.
        Closing += (_, _) =>
        {
            if (_completedSuccessfully) return;
            QuitApp();
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Open the wizard already positioned at the primer for a specific
    /// permission. Called by the tray "Fix permissions" item and the
    /// notification click handler.
    /// </summary>
    public void OpenToPrimer(string permissionId)
    {
        var target = permissionId switch
        {
            PermissionNetwork         => IndexOf("PageNetwork"),
            PermissionInputMonitoring => IndexOf("PageInputMonitoring"),
            _                         => 0,
        };
        if (target < 0) target = 0;
        ShowPage(target);
    }

    private int IndexOf(string controlName)
    {
        var c = this.FindControl<Grid>(controlName);
        if (c is null) return -1;
        for (var i = 0; i < _pages.Length; i++)
            if (ReferenceEquals(_pages[i], c)) return i;
        return -1;
    }

    private void ShowPage(int index)
    {
        if (_pages.Length == 0) return;
        if (index < 0) index = 0;
        if (index >= _pages.Length) index = _pages.Length - 1;
        _currentPageIndex = index;
        for (var i = 0; i < _pages.Length; i++)
            _pages[i].IsVisible = i == index;

        // Refresh per-page status text where applicable.
        if (ReferenceEquals(_pages[index], this.FindControl<Grid>("PageNetwork")))
            UpdateNetworkStatusLine();
        else if (ReferenceEquals(_pages[index], this.FindControl<Grid>("PageInputMonitoring")))
            UpdateInputMonitoringStatusLine();
    }

    private void AdvanceFrom(Grid currentPage)
    {
        for (var i = 0; i < _pages.Length; i++)
        {
            if (!ReferenceEquals(_pages[i], currentPage)) continue;
            var next = i + 1;
            if (next >= _pages.Length) next = _pages.Length - 1;
            ShowPage(next);
            return;
        }
        // Fall back to the next sequential page.
        ShowPage(_currentPageIndex + 1);
    }

    // ====================================================================
    // Page button handlers
    // ====================================================================

    private void OnWelcomeGetStarted(object? sender, RoutedEventArgs e)
    {
        AdvanceFrom(this.FindControl<Grid>("PageWelcome")!);
    }

    private async void OnNetworkContinue(object? sender, RoutedEventArgs e)
    {
        var btn = this.FindControl<Button>("NetworkContinueButton");
        if (btn is not null) btn.IsEnabled = false;
        var line = this.FindControl<TextBlock>("NetworkStatusLine");
        if (line is not null) line.Text = "Requesting Local Network access…";

        // NetworkPermission.Check() probes by attempting a 1-byte UDP send
        // to the topology multicast group on Mac — which is exactly what
        // triggers the macOS Local Network prompt the first time. On
        // Windows it inspects the network profile category (no prompt).
        await Task.Run(() =>
        {
            NetworkPermission.Invalidate();
            NetworkPermission.Check();
        });

        await Task.Delay(PromptSettleDelay);

        // Re-probe so the status line reflects the post-prompt state.
        NetworkPermission.Invalidate();
        var result = NetworkPermission.Check();
        if (line is not null) line.Text = result.Detail;

        AdvanceFrom(this.FindControl<Grid>("PageNetwork")!);
    }

    private void OnNetworkSkip(object? sender, RoutedEventArgs e)
    {
        AdvanceFrom(this.FindControl<Grid>("PageNetwork")!);
    }

    private async void OnInputMonitoringContinue(object? sender, RoutedEventArgs e)
    {
        var btn = this.FindControl<Button>("InputMonitoringContinueButton");
        if (btn is not null) btn.IsEnabled = false;
        var line = this.FindControl<TextBlock>("InputMonitoringStatusLine");
        if (line is not null) line.Text = "Requesting HID device access…";

        // Two-pronged nudge for the TCC prompt:
        //   1) Open the Input Monitoring system settings pane so the user
        //      can flip the toggle if the implicit prompt doesn't appear.
        //   2) Call IOHIDRequestAccess(kIOHIDRequestTypeListenEvent) — the
        //      official "prompt me, I want HID access" Apple API. This
        //      triggers the TCC dialog the first time the process asks.
        OpenInputMonitoringSettings();
        await Task.Run(() => InputMonitoringPermission.Request());

        await Task.Delay(PromptSettleDelay);

        var status = InputMonitoringPermission.Check();
        if (line is not null)
            line.Text = status switch
            {
                InputMonitoringPermission.Status.Granted => "HID device access: granted",
                InputMonitoringPermission.Status.Denied  => "HID device access: denied (you can enable it later in System Settings)",
                _                                        => "HID device access: pending (check System Settings if no prompt appeared)",
            };

        AdvanceFrom(this.FindControl<Grid>("PageInputMonitoring")!);
    }

    private void OnInputMonitoringSkip(object? sender, RoutedEventArgs e)
    {
        AdvanceFrom(this.FindControl<Grid>("PageInputMonitoring")!);
    }

    private void OnDoneOpen(object? sender, RoutedEventArgs e)
    {
        _completedSuccessfully = true;
        try
        {
            _settings.HasShownWelcome = true;
            _settings.Save();
        }
        catch
        {
            // Best-effort persist; an IO error here shouldn't trap the user
            // in the wizard. They'll just see it again next launch.
        }

        WelcomeCompleted?.Invoke();
        Dispatcher.UIThread.Post(Close);
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private void UpdateNetworkStatusLine()
    {
        var line = this.FindControl<TextBlock>("NetworkStatusLine");
        if (line is null) return;
        NetworkPermission.Invalidate();
        var res = NetworkPermission.Check();
        line.Text = res.Detail;
    }

    private void UpdateInputMonitoringStatusLine()
    {
        var line = this.FindControl<TextBlock>("InputMonitoringStatusLine");
        if (line is null) return;
        var status = InputMonitoringPermission.Check();
        line.Text = status switch
        {
            InputMonitoringPermission.Status.Granted        => "HID device access: granted",
            InputMonitoringPermission.Status.Denied         => "HID device access: denied",
            InputMonitoringPermission.Status.Unknown        => "HID device access: not yet requested",
            InputMonitoringPermission.Status.NotApplicable  => "HID device access: not applicable on this OS",
            _                                               => "HID device access: unknown",
        };
    }

    private static void OpenInputMonitoringSettings()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            Process.Start("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent");
        }
        catch
        {
            // best-effort
        }
    }

    private static void QuitApp()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
