using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.Core;
using BoltMate.Core.Permissions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;

namespace BoltMate.App.UI;

/// <summary>
/// View-model for the first-run welcome / fix-permissions wizard.
/// Owns state, the page state-machine, and the permission grant flow.
/// The window code-behind is reduced to Avalonia-specific plumbing —
/// Closing intercept, native QuitApp shutdown, XAML init, and forwarding
/// the WelcomeCompleted event up to the App layer.
/// </summary>
/// <remarks>
/// Lifecycle hardening that previously lived in the window:
/// <list type="bullet">
/// <item><see cref="_grantCts"/> is VM-owned and disposed in
///       <see cref="TeardownActivation"/>. Any in-flight Grant continuation
///       that races a close observes the cancel and bails cleanly.</item>
/// <item><see cref="_completedSuccessfully"/> is the happy-path latch the
///       window's Closing handler reads to distinguish "user finished" from
///       "user dismissed."</item>
/// <item>Page transitions never touch Avalonia handles — they mutate VM
///       properties. The window's XAML binds <c>IsVisible</c> per page to
///       the matching <c>ShowXxx</c> property.</item>
/// </list>
/// </remarks>
public sealed class WelcomeViewModel : ViewModelBase
{
    public const string PermissionNetwork = "network";
    public const string PermissionInputMonitoring = "input-monitoring";

    public const string PageWelcome = "PageWelcome";
    public const string PageNetworkPrimer = "PageNetworkPrimer";
    public const string PageNetworkRefusal = "PageNetworkRefusal";
    public const string PageInputMonitoringPrimer = "PageInputMonitoringPrimer";
    public const string PageInputMonitoringRefusal = "PageInputMonitoringRefusal";
    public const string PageDone = "PageDone";
    public const string PageLinux = "PageLinux";

    private static readonly string[] AllPageNames =
    {
        PageWelcome, PageNetworkPrimer, PageNetworkRefusal,
        PageInputMonitoringPrimer, PageInputMonitoringRefusal,
        PageDone, PageLinux,
    };

    private readonly AppSettings _settings;
    private readonly IPermissionsService _permissions;
    private readonly ILogger _log;
    private readonly bool _isFirstRun;
    private readonly CancellationTokenSource _grantCts = new();
    private IDisposable? _currentPageSubscription;
    private readonly HashSet<string> _primersShownThisSession = new();
    private bool _completedSuccessfully;
    private bool _torndown;

    /// <summary>Per-activation subscriptions. Disposed on TeardownActivation.</summary>
    public CompositeDisposable Activation { get; } = new();

    /// <summary>Window observes; raised once on the happy-path "Open BoltMate" press.</summary>
    public event Action? WelcomeCompleted;

    /// <summary>Window observes; raised when the user picks Quit BoltMate.</summary>
    public event Action<int>? QuitRequested;

    /// <summary>Window observes; raised when the wizard's happy-path close should fire.</summary>
    public event Action? CloseRequested;

    /// <summary>
    /// True once the user clicked Open BoltMate (or Linux fast-path).
    /// Window's Closing handler reads this to know NOT to quit the app.
    /// </summary>
    public bool CompletedSuccessfully => _completedSuccessfully;

    // ---- Page state ---------------------------------------------------

    private string _currentPage = PageWelcome;
    public string CurrentPage
    {
        get => _currentPage;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentPage, value);
            this.RaisePropertyChanged(nameof(ShowWelcome));
            this.RaisePropertyChanged(nameof(ShowNetworkPrimer));
            this.RaisePropertyChanged(nameof(ShowNetworkRefusal));
            this.RaisePropertyChanged(nameof(ShowInputMonitoringPrimer));
            this.RaisePropertyChanged(nameof(ShowInputMonitoringRefusal));
            this.RaisePropertyChanged(nameof(ShowDone));
            this.RaisePropertyChanged(nameof(ShowLinux));
        }
    }

    public bool ShowWelcome => CurrentPage == PageWelcome;
    public bool ShowNetworkPrimer => CurrentPage == PageNetworkPrimer;
    public bool ShowNetworkRefusal => CurrentPage == PageNetworkRefusal;
    public bool ShowInputMonitoringPrimer => CurrentPage == PageInputMonitoringPrimer;
    public bool ShowInputMonitoringRefusal => CurrentPage == PageInputMonitoringRefusal;
    public bool ShowDone => CurrentPage == PageDone;
    public bool ShowLinux => CurrentPage == PageLinux;

    // ---- Bindable display state ---------------------------------------

    private string _networkStatusLine = "";
    public string NetworkStatusLine
    {
        get => _networkStatusLine;
        set => this.RaiseAndSetIfChanged(ref _networkStatusLine, value);
    }

    private string _inputMonitoringStatusLine = "";
    public string InputMonitoringStatusLine
    {
        get => _inputMonitoringStatusLine;
        set => this.RaiseAndSetIfChanged(ref _inputMonitoringStatusLine, value);
    }

    private bool _networkPrimerGrantEnabled = true;
    public bool NetworkPrimerGrantEnabled
    {
        get => _networkPrimerGrantEnabled;
        set => this.RaiseAndSetIfChanged(ref _networkPrimerGrantEnabled, value);
    }

    private bool _networkRefusalGrantEnabled = true;
    public bool NetworkRefusalGrantEnabled
    {
        get => _networkRefusalGrantEnabled;
        set => this.RaiseAndSetIfChanged(ref _networkRefusalGrantEnabled, value);
    }

    private bool _inputMonitoringPrimerGrantEnabled = true;
    public bool InputMonitoringPrimerGrantEnabled
    {
        get => _inputMonitoringPrimerGrantEnabled;
        set => this.RaiseAndSetIfChanged(ref _inputMonitoringPrimerGrantEnabled, value);
    }

    private bool _inputMonitoringRefusalGrantEnabled = true;
    public bool InputMonitoringRefusalGrantEnabled
    {
        get => _inputMonitoringRefusalGrantEnabled;
        set => this.RaiseAndSetIfChanged(ref _inputMonitoringRefusalGrantEnabled, value);
    }

    private bool _autostartChecked = true;
    public bool AutostartChecked
    {
        get => _autostartChecked;
        set => this.RaiseAndSetIfChanged(ref _autostartChecked, value);
    }

    // ---- Commands -----------------------------------------------------

    public ReactiveCommand<Unit, Unit> GetStartedCommand { get; }
    public ReactiveCommand<Unit, Unit> NetworkPrimerGrantCommand { get; }
    public ReactiveCommand<Unit, Unit> NetworkPrimerNotNowCommand { get; }
    public ReactiveCommand<Unit, Unit> NetworkRefusalGrantCommand { get; }
    public ReactiveCommand<Unit, Unit> InputMonitoringPrimerGrantCommand { get; }
    public ReactiveCommand<Unit, Unit> InputMonitoringPrimerNotNowCommand { get; }
    public ReactiveCommand<Unit, Unit> InputMonitoringRefusalGrantCommand { get; }
    public ReactiveCommand<Unit, Unit> QuitCommand { get; }
    public ReactiveCommand<Unit, Unit> DoneOpenCommand { get; }

    public WelcomeViewModel(
        AppSettings settings,
        IPermissionsService permissions,
        bool isFirstRun,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(permissions);

        _settings = settings;
        _permissions = permissions;
        _isFirstRun = isFirstRun;
        _log = log ?? NullLogger.Instance;

        GetStartedCommand = ReactiveCommand.CreateFromTask(OnGetStartedAsync);
        NetworkPrimerGrantCommand = ReactiveCommand.CreateFromTask(() =>
            GrantOrRefuseAsync(_permissions.Network, refusalPage: PageNetworkRefusal,
                setEnabled: v => NetworkPrimerGrantEnabled = v));
        NetworkPrimerNotNowCommand = ReactiveCommand.Create(() =>
        {
            _log.LogInformation("User tapped Not now on network primer");
            CurrentPage = PageNetworkRefusal;
        });
        NetworkRefusalGrantCommand = ReactiveCommand.CreateFromTask(() =>
            GrantOrRefuseAsync(_permissions.Network, refusalPage: null,
                setEnabled: v => NetworkRefusalGrantEnabled = v));
        InputMonitoringPrimerGrantCommand = ReactiveCommand.CreateFromTask(() =>
            GrantOrRefuseAsync(_permissions.InputMonitoring, refusalPage: PageInputMonitoringRefusal,
                setEnabled: v => InputMonitoringPrimerGrantEnabled = v));
        InputMonitoringPrimerNotNowCommand = ReactiveCommand.Create(() =>
        {
            _log.LogInformation("User tapped Not now on HID primer");
            CurrentPage = PageInputMonitoringRefusal;
        });
        InputMonitoringRefusalGrantCommand = ReactiveCommand.CreateFromTask(() =>
            GrantOrRefuseAsync(_permissions.InputMonitoring, refusalPage: null,
                setEnabled: v => InputMonitoringRefusalGrantEnabled = v));
        QuitCommand = ReactiveCommand.Create(() =>
        {
            _log.LogInformation("User tapped Quit BoltMate");
            _completedSuccessfully = true; // suppress Closing-handler quit-again
            QuitRequested?.Invoke(1);
        });
        DoneOpenCommand = ReactiveCommand.CreateFromTask(OnDoneOpenAsync);
    }

    // ---- Activation lifecycle -----------------------------------------

    /// <summary>
    /// Begin the wizard from the natural entry point. On Linux, jumps to
    /// the fast-path "Open BoltMate" page. Otherwise starts at Welcome
    /// (first-run) or the first ungranted-permission primer
    /// (fix-permissions). Called by the window after construction.
    /// </summary>
    public void RunFlow()
    {
        if (OperatingSystem.IsLinux())
        {
            CurrentPage = PageLinux;
            return;
        }

        if (_isFirstRun)
        {
            if (!_settings.WelcomeStepCompleted)
            {
                ShowPage(PageWelcome);
                return;
            }
            _log.LogInformation("Wizard resume: welcome already done — advancing");
            AdvanceToNextRequiredPermissionOrDone();
            return;
        }

        // Fix-permissions mode: skip welcome, jump straight to first ungranted primer.
        AdvanceToNextRequiredPermissionOrDone();
    }

    /// <summary>
    /// Open the wizard already positioned at a specific primer. Called by
    /// the tray "Fix permissions…" item and the notification click.
    /// </summary>
    public void OpenToPrimer(string permissionId)
    {
        var target = permissionId switch
        {
            PermissionNetwork         => PageNetworkPrimer,
            PermissionInputMonitoring => PageInputMonitoringPrimer,
            _                         => PageNetworkPrimer,
        };
        ShowPage(target);
        UpdateStatusLines();
    }

    /// <summary>
    /// Disposes per-activation subscriptions + cancels any in-flight Grant
    /// continuation. Idempotent. Called by the window's Closing handler.
    /// </summary>
    public void TeardownActivation()
    {
        if (_torndown) return;
        _torndown = true;
        _currentPageSubscription?.Dispose();
        try { _grantCts.Cancel(); } catch (ObjectDisposedException) { /* belt + suspenders */ }
        try { _grantCts.Dispose(); } catch (ObjectDisposedException) { }
        Activation.Dispose();
    }

    // ---- State machine ------------------------------------------------

    private void ShowPage(string controlName)
    {
        CurrentPage = controlName;
        _currentPageSubscription?.Dispose();
        _currentPageSubscription = SubscribePageWatcher(controlName);
        UpdateStatusLines();
    }

    private IDisposable? SubscribePageWatcher(string pageName)
    {
        var permission = pageName switch
        {
            PageNetworkPrimer or PageNetworkRefusal => _permissions.Network,
            PageInputMonitoringPrimer or PageInputMonitoringRefusal => _permissions.InputMonitoring,
            _ => null,
        };
        if (permission is null) return null;

        return permission.IsGrantedChanged.Subscribe(granted =>
        {
            if (_torndown || _completedSuccessfully) return;
            try
            {
                UpdateStatusLines();
                if (granted)
                {
                    _log.LogInformation("Auto-advance: {Permission} granted while on {Page}", permission.Name, pageName);
                    SaveCheckpoint(permission.Name);
                    AdvanceToNextRequiredPermissionOrDone();
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Page watcher swallowed for {Permission}", permission.Name);
            }
        });
    }

    private void AdvanceToNextRequiredPermissionOrDone()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            _permissions.Refresh();
            var alreadyShown = _primersShownThisSession.Contains(PermissionNetwork);
            var requireShow = _isFirstRun && !alreadyShown && !_settings.NetworkStepCompleted;
            if (requireShow || !_permissions.Network.IsGranted)
            {
                _log.LogInformation("Permission gate: network granted={Granted}, firstRun={First}, alreadyShown={Shown}, ckpt={Ckpt} — showing primer",
                    _permissions.Network.IsGranted, _isFirstRun, alreadyShown, _settings.NetworkStepCompleted);
                _primersShownThisSession.Add(PermissionNetwork);
                ShowPage(PageNetworkPrimer);
                return;
            }
            SaveCheckpoint(PermissionNetwork);
        }

        if (OperatingSystem.IsMacOS())
        {
            var alreadyShown = _primersShownThisSession.Contains(PermissionInputMonitoring);
            var requireShow = _isFirstRun && !alreadyShown && !_settings.InputMonitoringStepCompleted;
            if (requireShow || !_permissions.InputMonitoring.IsGranted)
            {
                _log.LogInformation("Permission gate: input-monitoring granted={Granted}, firstRun={First}, alreadyShown={Shown}, ckpt={Ckpt} — showing primer",
                    _permissions.InputMonitoring.IsGranted, _isFirstRun, alreadyShown, _settings.InputMonitoringStepCompleted);
                _primersShownThisSession.Add(PermissionInputMonitoring);
                ShowPage(PageInputMonitoringPrimer);
                return;
            }
            SaveCheckpoint(PermissionInputMonitoring);
        }

        _log.LogInformation("Permission gate: all required permissions granted — Done");
        ShowPage(PageDone);
    }

    private void UpdateStatusLines()
    {
        NetworkStatusLine = _permissions.Network.IsGranted
            ? "Local Network access: granted"
            : "Local Network access: denied";
        InputMonitoringStatusLine = _permissions.InputMonitoring.IsGranted
            ? "HID device access: granted"
            : "HID device access: denied";
    }

    private void SaveCheckpoint(string step)
    {
        try
        {
            switch (step)
            {
                case PageWelcome:                       _settings.WelcomeStepCompleted = true; break;
                case PermissionNetwork:                 _settings.NetworkStepCompleted = true; break;
                case PermissionInputMonitoring:         _settings.InputMonitoringStepCompleted = true; break;
                case "welcome":                         _settings.WelcomeStepCompleted = true; break;
            }
            _settings.Save();
            _log.LogInformation("Wizard checkpoint saved: {Step}", step);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to save wizard checkpoint {Step}", step);
        }
    }

    // ---- Command implementations --------------------------------------

    private async Task OnGetStartedAsync()
    {
        _log.LogInformation("User clicked Get started");
        await ApplyAutostartFromToggleAsync();
        SaveCheckpoint("welcome");
        AdvanceToNextRequiredPermissionOrDone();
    }

    private async Task GrantOrRefuseAsync(IPermission permission, string? refusalPage, Action<bool> setEnabled)
    {
        setEnabled(false);

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
        // a Quit AppleEvent after an HID grant to force a relaunch. VM
        // mutation below stays safe (no Avalonia handles), but we still
        // need to bail to avoid surfacing a refusal page on a closing window.
        if (_torndown || _completedSuccessfully) return;
        try
        {
            setEnabled(true);
            if (!granted && refusalPage is not null && CurrentPage != refusalPage)
            {
                ShowPage(refusalPage);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GrantOrRefuseAsync post-await update threw — window likely closing");
        }
    }

    private async Task OnDoneOpenAsync()
    {
        _log.LogInformation("User tapped Open BoltMate (Done)");
        _completedSuccessfully = true;

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
        CloseRequested?.Invoke();
    }

    private async Task ApplyAutostartFromToggleAsync()
    {
        if (!AppAutostart.CanRegister())
        {
            _log.LogInformation("Autostart not applicable (running from 'dotnet run' or unknown binary path)");
            return;
        }
        try
        {
            // Off the UI thread: launchctl / reg.exe spawn child processes
            // that can take 10s of ms each. Sync would freeze the wizard.
            var want = AutostartChecked;
            var result = await Task.Run(() => want ? AppAutostart.Install() : AppAutostart.Uninstall());
            _log.LogInformation("Autostart {Action}: success={Ok} message={Msg}",
                want ? "install" : "uninstall", result.Success, result.Message);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Autostart toggle apply failed (non-fatal)");
        }
    }
}
