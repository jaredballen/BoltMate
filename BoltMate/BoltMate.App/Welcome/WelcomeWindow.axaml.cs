using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BoltMate.Core;
using BoltMate.Hid.IOKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.App.Welcome;

/// <summary>
/// First-run welcome wizard AND on-demand "Fix permissions" entry point.
///
/// The wizard is a state machine, not a linear page list. Pages exist for
/// every state; advance is driven by code-behind based on the user's
/// explicit Grant / Not now / Quit press and the post-press OS permission
/// recheck. No page auto-fires an OS prompt — every prompt is tied to a
/// Grant button press.
///
/// State outline:
///
///   Welcome ─► (autostart toggle staged)
///       │
///       ▼  Get started
///   NetworkPrimer ◄────────────────┐
///       │  Grant                   │
///       ▼                          │
///   [Request + recheck]            │
///       │                          │
///       ├──► granted ──►  next required perm OR Done
///       │                          │
///       └──► still denied / "Not now"
///                       │          │
///                       ▼          │
///                  NetworkRefusal  │
///                       │ Grant ───┘
///                       │ Quit ──► Shutdown(1)
///
/// HID flow (Mac only) is the same shape, sequenced AFTER network. On Win
/// + Linux the HID pages are never visited.
///
/// Modal-ish contract:
///   • Closing the window via [x] / Cmd-W counts as the user opting OUT
///     and quits the app (same as Quit BoltMate button).
///   • Closing via the "Open BoltMate" / "Done" path is the happy exit:
///     applies autostart toggle, flips <see cref="AppSettings.HasShownWelcome"/>
///     true, saves, fires <see cref="WelcomeCompleted"/>, and lets App layer
///     continue bootstrap.
///
/// "Fix permissions" mode (subsequent launches) reuses this same window via
/// <see cref="OpenToPrimer"/> — but skips the Welcome page (autostart is
/// configured in Settings at that point, not here), and on completion does
/// NOT flip HasShownWelcome (it's already true). The Closing handler also
/// does NOT quit in fix-permissions mode — the user can just close it.
/// </summary>
public partial class WelcomeWindow : Window
{
    public const string PermissionNetwork = "network";
    public const string PermissionInputMonitoring = "input-monitoring";

    // How long we wait between issuing a Grant request and re-checking the
    // permission status. Long enough for the OS prompt to surface AND for
    // the user to make a decision in many cases — but we also re-check on
    // window focus regain so a slow user doesn't get penalised.
    private static readonly TimeSpan PromptSettleDelay = TimeSpan.FromMilliseconds(2000);

    private readonly AppSettings _settings;
    private readonly ILogger _log;
    private readonly bool _isFirstRun;

    // Flips to true on the happy path (Done / Open BoltMate button). The
    // Closing handler uses this to distinguish "user finished" from "user
    // dismissed" — the latter quits the app on first run.
    private bool _completedSuccessfully;

    /// <summary>
    /// Fired exactly once on the happy path right before the window closes.
    /// The App layer subscribes to this to start the rest of bootstrap +
    /// open Settings.
    /// </summary>
    public event Action? WelcomeCompleted;

    /// <summary>Designer-only ctor. Real usage goes through the (AppSettings, …) ctor.</summary>
    public WelcomeWindow() : this(new AppSettings(), isFirstRun: true, NullLogger.Instance) { }

    public WelcomeWindow(AppSettings settings, bool isFirstRun = true, ILogger? log = null)
    {
        _settings = settings;
        _isFirstRun = isFirstRun;
        _log = log ?? NullLogger.Instance;
        InitializeComponent();

        ShowPage("PageWelcome");

        // Intercept manual close: on first run, treat any non-happy-path
        // dismiss as a quit (per spec). In Fix-Permissions mode just close
        // silently — the rest of the app is already running.
        Closing += (_, _) =>
        {
            if (_completedSuccessfully) return;
            if (_isFirstRun)
            {
                _log.LogInformation("Welcome wizard dismissed without completion — quitting app");
                QuitApp();
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Begin the welcome flow from the natural entry point. On Linux,
    /// short-circuits to the fast-path "Open BoltMate" page. Otherwise
    /// starts at the Welcome page (first-run) or the first ungranted
    /// permission primer (fix-permissions).
    /// </summary>
    public void RunFlow()
    {
        if (OperatingSystem.IsLinux())
        {
            ShowPage("PageLinux");
            return;
        }

        if (_isFirstRun)
        {
            ShowPage("PageWelcome");
            return;
        }

        // Fix-permissions mode: skip the welcome page and jump straight to
        // the first primer for a still-ungranted permission. If everything
        // is granted just show Done.
        AdvanceToNextRequiredPermissionOrDone();
    }

    /// <summary>
    /// Open the wizard already positioned at the primer for a specific
    /// permission. Called by the tray "Fix permissions" item and the
    /// notification click handler. Treated as fix-permissions mode (no
    /// HasShownWelcome flip).
    /// </summary>
    public void OpenToPrimer(string permissionId)
    {
        var targetPage = permissionId switch
        {
            PermissionNetwork         => "PageNetworkPrimer",
            PermissionInputMonitoring => "PageInputMonitoringPrimer",
            _                         => "PageNetworkPrimer",
        };
        ShowPage(targetPage);
        UpdateStatusLineForCurrentPage();
    }

    // ====================================================================
    // Page transitions
    // ====================================================================

    private void ShowPage(string controlName)
    {
        // Hide every page, then show the named one.
        foreach (var pageName in AllPageNames)
        {
            var g = this.FindControl<Grid>(pageName);
            if (g is not null) g.IsVisible = false;
        }
        var target = this.FindControl<Grid>(controlName);
        if (target is not null) target.IsVisible = true;
        UpdateStatusLineForCurrentPage();
    }

    private static readonly string[] AllPageNames =
    {
        "PageWelcome",
        "PageNetworkPrimer",
        "PageNetworkRefusal",
        "PageInputMonitoringPrimer",
        "PageInputMonitoringRefusal",
        "PageDone",
        "PageLinux",
    };

    /// <summary>
    /// Walks the per-platform required-permission list and shows the primer
    /// for the first one that is currently NOT granted. If every required
    /// permission is already granted, jumps to Done.
    /// </summary>
    private void AdvanceToNextRequiredPermissionOrDone()
    {
        // Network is required on Mac + Windows.
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            NetworkPermission.Invalidate();
            var net = NetworkPermission.Check();
            if (net.Status != NetworkPermission.Status.Granted)
            {
                _log.LogInformation("Permission gate: network = {Status} — showing primer", net.Status);
                ShowPage("PageNetworkPrimer");
                return;
            }
        }

        // HID (Input Monitoring) is Mac-only.
        if (OperatingSystem.IsMacOS())
        {
            var im = InputMonitoringPermission.Check();
            if (im != InputMonitoringPermission.Status.Granted)
            {
                _log.LogInformation("Permission gate: input-monitoring = {Status} — showing primer", im);
                ShowPage("PageInputMonitoringPrimer");
                return;
            }
        }

        _log.LogInformation("Permission gate: all required permissions granted — Done");
        ShowPage("PageDone");
    }

    // ====================================================================
    // Page button handlers
    // ====================================================================

    private void OnWelcomeGetStarted(object? sender, RoutedEventArgs e)
    {
        _log.LogInformation("User clicked Get started");
        AdvanceToNextRequiredPermissionOrDone();
    }

    // ---- Network primer ------------------------------------------------

    private async void OnNetworkPrimerGrant(object? sender, RoutedEventArgs e)
    {
        _log.LogInformation("User tapped Grant on network primer");
        await RequestNetworkAndAdvance(refusalIfDenied: true);
    }

    private void OnNetworkPrimerNotNow(object? sender, RoutedEventArgs e)
    {
        _log.LogInformation("User tapped Not now on network primer");
        ShowPage("PageNetworkRefusal");
    }

    // ---- Network refusal -----------------------------------------------

    private async void OnNetworkRefusalGrant(object? sender, RoutedEventArgs e)
    {
        _log.LogInformation("User tapped Grant on network refusal");
        // Try again from the refusal page. If still denied, stay on
        // refusal (don't bounce to primer — that's a worse UX).
        await RequestNetworkAndAdvance(refusalIfDenied: false);
    }

    // ---- HID primer ----------------------------------------------------

    private async void OnInputMonitoringPrimerGrant(object? sender, RoutedEventArgs e)
    {
        _log.LogInformation("User tapped Grant on HID primer");
        await RequestInputMonitoringAndAdvance(refusalIfDenied: true);
    }

    private void OnInputMonitoringPrimerNotNow(object? sender, RoutedEventArgs e)
    {
        _log.LogInformation("User tapped Not now on HID primer");
        ShowPage("PageInputMonitoringRefusal");
    }

    // ---- HID refusal ---------------------------------------------------

    private async void OnInputMonitoringRefusalGrant(object? sender, RoutedEventArgs e)
    {
        _log.LogInformation("User tapped Grant on HID refusal");
        await RequestInputMonitoringAndAdvance(refusalIfDenied: false);
    }

    // ---- Quit (refusal pages) ------------------------------------------

    private void OnQuitApp(object? sender, RoutedEventArgs e)
    {
        _log.LogInformation("User tapped Quit BoltMate");
        // Don't run Closing-handler quit-logic — call Shutdown directly.
        _completedSuccessfully = true; // suppress Closing-handler quit
        QuitApp(exitCode: 1);
    }

    // ---- Done ----------------------------------------------------------

    private void OnDoneOpen(object? sender, RoutedEventArgs e)
    {
        _log.LogInformation("User tapped Open BoltMate (Done)");
        _completedSuccessfully = true;

        // Apply autostart toggle (first-run only — fix-permissions mode
        // doesn't surface the welcome page so the checkbox was never
        // touched).
        if (_isFirstRun)
        {
            ApplyAutostartFromToggle();
            try
            {
                _settings.HasShownWelcome = true;
                _settings.Save();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to persist HasShownWelcome — user will see the wizard again next launch");
            }
        }

        WelcomeCompleted?.Invoke();
        Dispatcher.UIThread.Post(Close);
    }

    // ====================================================================
    // Permission request helpers
    // ====================================================================

    private async Task RequestNetworkAndAdvance(bool refusalIfDenied)
    {
        var grantBtn = this.FindControl<Button>(refusalIfDenied
            ? "NetworkPrimerGrantButton"
            : "NetworkRefusalGrantButton");
        var statusLine = this.FindControl<TextBlock>(refusalIfDenied
            ? "NetworkPrimerStatusLine"
            : "NetworkRefusalStatusLine");

        if (grantBtn is not null) grantBtn.IsEnabled = false;
        if (statusLine is not null) statusLine.Text = "Requesting Local Network access…";

        var requested = await Task.Run(NetworkPermission.Request);
        _log.LogInformation("NetworkPermission.Request returned {Result}", requested);

        await Task.Delay(PromptSettleDelay);

        NetworkPermission.Invalidate();
        var result = NetworkPermission.Check();
        _log.LogInformation("Post-request network status: {Status} ({Detail})", result.Status, result.Detail);

        if (grantBtn is not null) grantBtn.IsEnabled = true;
        if (statusLine is not null) statusLine.Text = result.Detail;

        if (result.Status == NetworkPermission.Status.Granted)
        {
            // Network is good — proceed to next required permission, or
            // Done if nothing else.
            AdvanceToNextRequiredPermissionOrDone();
        }
        else if (refusalIfDenied)
        {
            ShowPage("PageNetworkRefusal");
            var refusalLine = this.FindControl<TextBlock>("NetworkRefusalStatusLine");
            if (refusalLine is not null) refusalLine.Text = result.Detail;
        }
        // else: stay on refusal page (we're already there)
    }

    private async Task RequestInputMonitoringAndAdvance(bool refusalIfDenied)
    {
        var grantBtn = this.FindControl<Button>(refusalIfDenied
            ? "InputMonitoringPrimerGrantButton"
            : "InputMonitoringRefusalGrantButton");
        var statusLine = this.FindControl<TextBlock>(refusalIfDenied
            ? "InputMonitoringPrimerStatusLine"
            : "InputMonitoringRefusalStatusLine");

        if (grantBtn is not null) grantBtn.IsEnabled = false;
        if (statusLine is not null) statusLine.Text = "Requesting HID device access…";

        // IOHIDRequestAccess(kIOHIDRequestTypeListenEvent) — official Apple
        // API for "prompt the user for HID access". Foreground first so the
        // TCC dialog can attach to our window.
        MacActivationPolicy.ShowDockIcon();
        await Task.Delay(100);
        var requested = await Task.Run(InputMonitoringPermission.Request);
        _log.LogInformation("InputMonitoringPermission.Request returned {Result}", requested);

        await Task.Delay(PromptSettleDelay);

        var status = InputMonitoringPermission.Check();
        _log.LogInformation("Post-request HID status: {Status}", status);

        if (grantBtn is not null) grantBtn.IsEnabled = true;
        if (statusLine is not null)
            statusLine.Text = HidStatusToDetail(status);

        if (status == InputMonitoringPermission.Status.Granted)
        {
            AdvanceToNextRequiredPermissionOrDone();
        }
        else if (refusalIfDenied)
        {
            ShowPage("PageInputMonitoringRefusal");
            var refusalLine = this.FindControl<TextBlock>("InputMonitoringRefusalStatusLine");
            if (refusalLine is not null) refusalLine.Text = HidStatusToDetail(status);
        }
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private void UpdateStatusLineForCurrentPage()
    {
        var pageName = CurrentPageName();
        switch (pageName)
        {
            case "PageNetworkPrimer":
            case "PageNetworkRefusal":
                {
                    NetworkPermission.Invalidate();
                    var res = NetworkPermission.Check();
                    var line = this.FindControl<TextBlock>(
                        pageName == "PageNetworkPrimer"
                            ? "NetworkPrimerStatusLine"
                            : "NetworkRefusalStatusLine");
                    if (line is not null) line.Text = res.Detail;
                    break;
                }
            case "PageInputMonitoringPrimer":
            case "PageInputMonitoringRefusal":
                {
                    var status = InputMonitoringPermission.Check();
                    var line = this.FindControl<TextBlock>(
                        pageName == "PageInputMonitoringPrimer"
                            ? "InputMonitoringPrimerStatusLine"
                            : "InputMonitoringRefusalStatusLine");
                    if (line is not null) line.Text = HidStatusToDetail(status);
                    break;
                }
        }
    }

    private string? CurrentPageName()
    {
        foreach (var pageName in AllPageNames)
        {
            var g = this.FindControl<Grid>(pageName);
            if (g?.IsVisible == true) return pageName;
        }
        return null;
    }

    private static string HidStatusToDetail(InputMonitoringPermission.Status status) => status switch
    {
        InputMonitoringPermission.Status.Granted        => "HID device access: granted",
        InputMonitoringPermission.Status.Denied         => "HID device access: denied",
        InputMonitoringPermission.Status.Unknown        => "HID device access: pending (check System Settings if no prompt appeared)",
        InputMonitoringPermission.Status.NotApplicable  => "HID device access: not applicable on this OS",
        _                                               => "HID device access: unknown",
    };

    private void ApplyAutostartFromToggle()
    {
        var toggle = this.FindControl<CheckBox>("WelcomeAutostartToggle");
        var want = toggle?.IsChecked == true;
        if (!AppAutostart.CanRegister())
        {
            _log.LogInformation("Autostart not applicable (running from 'dotnet run' or unknown binary path)");
            return;
        }
        try
        {
            var result = want ? AppAutostart.Install() : AppAutostart.Uninstall();
            _log.LogInformation("Autostart {Action}: success={Ok} message={Msg}",
                want ? "install" : "uninstall", result.Success, result.Message);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Autostart toggle apply failed (non-fatal)");
        }
    }

    private static void QuitApp(int exitCode = 0)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown(exitCode);
    }
}
