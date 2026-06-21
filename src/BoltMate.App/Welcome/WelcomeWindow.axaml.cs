using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BoltMate.App.Permissions;
using BoltMate.Core;
using BoltMate.Core.Permissions;
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

    private readonly AppSettings _settings;
    private readonly IPermissionsService _permissions;
    private readonly ILogger _log;
    private readonly bool _isFirstRun;
    private readonly CompositeDisposable _disposables = new();
    private readonly CancellationTokenSource _grantCts = new();
    private IDisposable? _currentPageSubscription;

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

    /// <summary>Designer-only ctor. Real usage goes through the (AppSettings, IPermissionsService, …) ctor.</summary>
    public WelcomeWindow() : this(new AppSettings(), new PermissionsService(), isFirstRun: true, NullLogger.Instance) { }

    public WelcomeWindow(AppSettings settings, IPermissionsService permissions, bool isFirstRun = true, ILogger? log = null)
    {
        _settings = settings;
        _permissions = permissions;
        _isFirstRun = isFirstRun;
        _log = log ?? NullLogger.Instance;
        InitializeComponent();

        ShowPage("PageWelcome");

        // Intercept manual close: on first run, treat any non-happy-path
        // dismiss as a quit (per spec). In Fix-Permissions mode just close
        // silently — the rest of the app is already running.
        Closing += (_, _) =>
        {
            // Guard against re-entry. QuitApp -> desktop.Shutdown() will fire
            // Close on every window, causing this Closing handler to fire
            // again. Without the guard we'd recurse to stack overflow (and
            // we crashed exactly like this when macOS sent a Quit AppleEvent
            // after the Input Monitoring grant required us to relaunch).
            _currentPageSubscription?.Dispose();
            _grantCts.Cancel();
            _grantCts.Dispose();
            _disposables.Dispose();
            if (_completedSuccessfully || _quitting) return;
            if (_isFirstRun)
            {
                _quitting = true;
                _log.LogInformation("Welcome wizard dismissed without completion — quitting app");
                QuitApp();
            }
        };
    }

    private bool _quitting;

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
            // Resume mid-wizard if the user already cleared some steps in a
            // prior session. The most common cause is macOS forcing a
            // relaunch after the user grants Input Monitoring in System
            // Settings — we land here on the next startup with
            // WelcomeStepCompleted + NetworkStepCompleted already true, so
            // we should jump straight to PageInputMonitoringPrimer.
            if (!_settings.WelcomeStepCompleted)
            {
                ShowPage("PageWelcome");
                return;
            }
            _log.LogInformation("Wizard resume: welcome already done — advancing");
            AdvanceToNextRequiredPermissionOrDone();
            return;
        }

        // Fix-permissions mode: skip the welcome page and jump straight to
        // the first primer for a still-ungranted permission. If everything
        // is granted just show Done.
        AdvanceToNextRequiredPermissionOrDone();
    }

    /// <summary>
    /// Persist a step-completion flag immediately. Best-effort — settings
    /// save failures are non-fatal (worst case: re-show the same page on
    /// next launch).
    /// </summary>
    private void SaveCheckpoint(string step)
    {
        try
        {
            switch (step)
            {
                case "welcome": _settings.WelcomeStepCompleted = true; break;
                case "network": _settings.NetworkStepCompleted = true; break;
                case "input-monitoring": _settings.InputMonitoringStepCompleted = true; break;
            }
            _settings.Save();
            _log.LogInformation("Wizard checkpoint saved: {Step}", step);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to save wizard checkpoint {Step}", step);
        }
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

        // Resubscribe the page-scoped permission watcher. While a primer or
        // refusal page is visible we listen to the relevant IPermission and:
        //   • refresh the status line on every emit
        //   • auto-advance when it flips to Granted (user toggled in System
        //     Settings, or hit Allow on the OS prompt without us seeing return)
        _currentPageSubscription?.Dispose();
        _currentPageSubscription = SubscribePageWatcher(controlName);

        UpdateStatusLineForCurrentPage();
    }

    private IDisposable? SubscribePageWatcher(string pageName)
    {
        var permission = pageName switch
        {
            "PageNetworkPrimer" or "PageNetworkRefusal" => _permissions.Network,
            "PageInputMonitoringPrimer" or "PageInputMonitoringRefusal" => _permissions.InputMonitoring,
            _ => null,
        };
        if (permission is null) return null;

        // Service's polling timer runs on the UI dispatcher, so emits arrive
        // on the UI thread already — no ObserveOn needed.
        return permission.IsGrantedChanged
            .Subscribe(granted =>
            {
                UpdateStatusLineForCurrentPage();
                if (granted)
                {
                    _log.LogInformation("Auto-advance: {Permission} granted while on {Page}", permission.Name, pageName);
                    SaveCheckpoint(permission.Name);
                    AdvanceToNextRequiredPermissionOrDone();
                }
            });
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
    // Once a primer has been shown in this WelcomeWindow session we don't
    // re-present it even if the user hasn't granted yet — they advance via
    // Grant/Not-now buttons. Without this tracking the Check()-based gate
    // could loop the user back to the same primer after they tap Grant.
    private readonly HashSet<string> _primersShownThisSession = new();

    private void AdvanceToNextRequiredPermissionOrDone()
    {
        // Network is required on Mac + Windows.
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            _permissions.Refresh();
            var alreadyShown = _primersShownThisSession.Contains(PermissionNetwork);
            // On first-run we still show the network primer at least once
            // even when already Granted, so users see the explainer copy and
            // know what the LAN traffic is for. Skip if a prior session
            // already completed this step.
            var requireShow = _isFirstRun && !alreadyShown && !_settings.NetworkStepCompleted;
            if (requireShow || !_permissions.Network.IsGranted)
            {
                _log.LogInformation("Permission gate: network granted={Granted}, firstRun={First}, alreadyShown={Shown}, ckpt={Ckpt} — showing primer",
                    _permissions.Network.IsGranted, _isFirstRun, alreadyShown, _settings.NetworkStepCompleted);
                _primersShownThisSession.Add(PermissionNetwork);
                ShowPage("PageNetworkPrimer");
                return;
            }
            SaveCheckpoint("network");
        }

        // HID (Input Monitoring) is Mac-only.
        if (OperatingSystem.IsMacOS())
        {
            var alreadyShown = _primersShownThisSession.Contains(PermissionInputMonitoring);
            var requireShow = _isFirstRun && !alreadyShown && !_settings.InputMonitoringStepCompleted;
            if (requireShow || !_permissions.InputMonitoring.IsGranted)
            {
                _log.LogInformation("Permission gate: input-monitoring granted={Granted}, firstRun={First}, alreadyShown={Shown}, ckpt={Ckpt} — showing primer",
                    _permissions.InputMonitoring.IsGranted, _isFirstRun, alreadyShown, _settings.InputMonitoringStepCompleted);
                _primersShownThisSession.Add(PermissionInputMonitoring);
                ShowPage("PageInputMonitoringPrimer");
                return;
            }
            SaveCheckpoint("input-monitoring");
        }

        _log.LogInformation("Permission gate: all required permissions granted — Done");
        ShowPage("PageDone");
    }

    // ====================================================================
    // Page button handlers
    // ====================================================================

    private async void OnWelcomeGetStarted(object? sender, RoutedEventArgs e)
    {
        _log.LogInformation("User clicked Get started");
        // Apply the autostart toggle NOW (not on the Done page) so that if a
        // permission prompt later forces the app to relaunch (macOS HID
        // grant), the user's choice has already taken effect at the OS level.
        await ApplyAutostartFromToggleAsync();
        SaveCheckpoint("welcome");
        AdvanceToNextRequiredPermissionOrDone();
    }

    // ---- Network primer ------------------------------------------------

    private async void OnNetworkPrimerGrant(object? sender, RoutedEventArgs e)
    {
        _log.LogInformation("User tapped Grant on network primer");
        await GrantOrRefuseAsync(_permissions.Network, refusalPage: "PageNetworkRefusal", primerButton: "NetworkPrimerGrantButton");
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
        await GrantOrRefuseAsync(_permissions.Network, refusalPage: null, primerButton: "NetworkRefusalGrantButton");
    }

    // ---- HID primer ----------------------------------------------------

    private async void OnInputMonitoringPrimerGrant(object? sender, RoutedEventArgs e)
    {
        _log.LogInformation("User tapped Grant on HID primer");
        await GrantOrRefuseAsync(_permissions.InputMonitoring, refusalPage: "PageInputMonitoringRefusal", primerButton: "InputMonitoringPrimerGrantButton");
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
        await GrantOrRefuseAsync(_permissions.InputMonitoring, refusalPage: null, primerButton: "InputMonitoringRefusalGrantButton");
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

    private async void OnDoneOpen(object? sender, RoutedEventArgs e)
    {
        _log.LogInformation("User tapped Open BoltMate (Done)");
        _completedSuccessfully = true;

        // Apply autostart toggle (first-run only — fix-permissions mode
        // doesn't surface the welcome page so the checkbox was never
        // touched).
        if (_isFirstRun)
        {
            await ApplyAutostartFromToggleAsync();
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
    // Permission grant helpers
    // ====================================================================

    /// <summary>
    /// Common Grant-button handler. Disables the button while
    /// <see cref="IPermission.GrantAsync"/> drives the permission toward
    /// Granted — GrantAsync itself awaits the underlying observable until
    /// it flips, so no per-call polling delay is needed here. Cancellation
    /// is wired to the window-scoped <see cref="_grantCts"/> so navigating
    /// away or closing the wizard tears the in-flight call down cleanly.
    /// </summary>
    private async Task GrantOrRefuseAsync(IPermission permission, string? refusalPage, string primerButton)
    {
        var grantBtn = this.FindControl<Button>(primerButton);
        if (grantBtn is not null) grantBtn.IsEnabled = false;

        bool granted = false;
        try
        {
            granted = await permission.GrantAsync(_grantCts.Token);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GrantAsync threw for {Permission}", permission.Name);
        }

        // After the await, the window may have started closing — macOS sends
        // a Quit AppleEvent after an HID grant to force a relaunch with the
        // new entitlement. Touching FindControl / ShowPage on a closing
        // window throws on Avalonia 12 (XPlatHandle disposed), and because
        // this method runs as the continuation of an async void event
        // handler, an uncaught throw aborts the process. Guard everything.
        if (_quitting || _completedSuccessfully) return;
        try
        {
            if (grantBtn is not null) grantBtn.IsEnabled = true;
            if (!granted && refusalPage is not null && CurrentPageName() != refusalPage)
            {
                ShowPage(refusalPage);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GrantOrRefuseAsync post-await UI update threw — window likely closing");
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
                    var line = this.FindControl<TextBlock>(
                        pageName == "PageNetworkPrimer"
                            ? "NetworkPrimerStatusLine"
                            : "NetworkRefusalStatusLine");
                    if (line is not null)
                        line.Text = _permissions.Network.IsGranted
                            ? "Local Network access: granted"
                            : "Local Network access: denied";
                    break;
                }
            case "PageInputMonitoringPrimer":
            case "PageInputMonitoringRefusal":
                {
                    var line = this.FindControl<TextBlock>(
                        pageName == "PageInputMonitoringPrimer"
                            ? "InputMonitoringPrimerStatusLine"
                            : "InputMonitoringRefusalStatusLine");
                    if (line is not null)
                        line.Text = _permissions.InputMonitoring.IsGranted
                            ? "HID device access: granted"
                            : "HID device access: denied";
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

    private async Task ApplyAutostartFromToggleAsync()
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
            // Off the UI thread: launchctl / reg.exe spawn child processes
            // that can take 10s of ms each. Keeping it sync froze the
            // welcome window on Win during the Get-Started click.
            var result = await Task.Run(() => want ? AppAutostart.Install() : AppAutostart.Uninstall());
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
