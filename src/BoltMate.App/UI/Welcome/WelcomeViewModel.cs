using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    public const string PermissionNotifications = "notifications";

    public const string PageSignIn = "PageSignIn";
    public const string PageWelcome = "PageWelcome";
    public const string PageNetworkPrimer = "PageNetworkPrimer";
    public const string PageNetworkRefusal = "PageNetworkRefusal";
    public const string PageInputMonitoringPrimer = "PageInputMonitoringPrimer";
    public const string PageInputMonitoringRefusal = "PageInputMonitoringRefusal";
    public const string PageNotificationsPrimer = "PageNotificationsPrimer";
    public const string PageDone = "PageDone";
    public const string PageLinux = "PageLinux";

    private static readonly string[] AllPageNames =
    {
        PageSignIn, PageWelcome, PageNetworkPrimer, PageNetworkRefusal,
        PageInputMonitoringPrimer, PageInputMonitoringRefusal,
        PageNotificationsPrimer,
        PageDone, PageLinux,
    };

    private readonly AppSettings _settings;
    private readonly IPermissionsService _permissions;
    private readonly BoltMate.Licensing.ILicenseGate? _licenseGate;
    private readonly BoltMate.App.Core.Notifications.INotificationService? _notifications;
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

    private string _currentPage = PageSignIn;
    public string CurrentPage
    {
        get => _currentPage;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentPage, value);
            this.RaisePropertyChanged(nameof(ShowSignIn));
            this.RaisePropertyChanged(nameof(ShowWelcome));
            this.RaisePropertyChanged(nameof(ShowNetworkPrimer));
            this.RaisePropertyChanged(nameof(ShowNetworkRefusal));
            this.RaisePropertyChanged(nameof(ShowInputMonitoringPrimer));
            this.RaisePropertyChanged(nameof(ShowInputMonitoringRefusal));
            this.RaisePropertyChanged(nameof(ShowNotificationsPrimer));
            this.RaisePropertyChanged(nameof(ShowDone));
            this.RaisePropertyChanged(nameof(ShowLinux));
            this.RaisePropertyChanged(nameof(ShowSpine));
            UpdateStepStates();
        }
    }

    public bool ShowSignIn => CurrentPage == PageSignIn;
    public bool ShowWelcome => CurrentPage == PageWelcome;
    public bool ShowNetworkPrimer => CurrentPage == PageNetworkPrimer;
    public bool ShowNetworkRefusal => CurrentPage == PageNetworkRefusal;
    public bool ShowInputMonitoringPrimer => CurrentPage == PageInputMonitoringPrimer;
    public bool ShowInputMonitoringRefusal => CurrentPage == PageInputMonitoringRefusal;
    public bool ShowNotificationsPrimer => CurrentPage == PageNotificationsPrimer;
    public bool ShowDone => CurrentPage == PageDone;
    public bool ShowLinux => CurrentPage == PageLinux;

    // ---- Left-rail step tracker --------------------------------------
    //
    // Per-platform step layout matches the handoff:
    //   • macOS: Welcome → Local Network → Input Monitoring → Notifications → Done  (5)
    //   • Windows: Welcome → Windows Firewall → Notifications → Done                 (4)
    //   • Linux: a single "Linux fast-path" item (rail still renders for parity)
    //
    // Steps are constructed once at VM creation; only the per-step State
    // mutates as CurrentPage advances. The window's XAML binds the
    // ItemsControl + StepCaption to these properties.

    public ObservableCollection<WizardStep> Steps { get; } = new();

    public const string StepKeyWelcome             = "welcome";
    public const string StepKeyLocalNetwork        = "local-network";
    public const string StepKeyInputMonitoring     = "input-monitoring";
    public const string StepKeyFirewall            = "firewall";
    public const string StepKeyNotifications       = "notifications";
    public const string StepKeyDone                = "done";

    private string _stepCaption = "";
    /// <summary>"Step N of M" caption shown above the rail's step list.</summary>
    public string StepCaption
    {
        get => _stepCaption;
        private set => this.RaiseAndSetIfChanged(ref _stepCaption, value);
    }

    /// <summary>Spine on the Welcome step's left edge — design spec calls for the 3 px green stroke only there.</summary>
    public bool ShowSpine => CurrentPage == PageWelcome;

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

    private string _notificationsStatusLine = "";
    public string NotificationsStatusLine
    {
        get => _notificationsStatusLine;
        set => this.RaiseAndSetIfChanged(ref _notificationsStatusLine, value);
    }

    // Status-pill state mirroring Settings card, so the primer feels like
    // the same control surface.
    private string _notificationsPillText = "Disabled";
    public string NotificationsPillText
    {
        get => _notificationsPillText;
        set => this.RaiseAndSetIfChanged(ref _notificationsPillText, value);
    }

    private string _notificationsPillBackground = "#1A9CA3AF";
    public string NotificationsPillBackground
    {
        get => _notificationsPillBackground;
        set => this.RaiseAndSetIfChanged(ref _notificationsPillBackground, value);
    }

    private string _notificationsPillForeground = "#9CA3AF";
    public string NotificationsPillForeground
    {
        get => _notificationsPillForeground;
        set => this.RaiseAndSetIfChanged(ref _notificationsPillForeground, value);
    }

    private string _notificationsPillDot = "#9CA3AF";
    public string NotificationsPillDot
    {
        get => _notificationsPillDot;
        set => this.RaiseAndSetIfChanged(ref _notificationsPillDot, value);
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
    public ReactiveCommand<Unit, Unit> NotificationsPrimerOpenSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> NotificationsPrimerContinueCommand { get; }
    public ReactiveCommand<Unit, Unit> QuitCommand { get; }
    public ReactiveCommand<Unit, Unit> DoneOpenCommand { get; }

    public WelcomeViewModel(
        AppSettings settings,
        IPermissionsService permissions,
        bool isFirstRun,
        BoltMate.App.Core.Notifications.INotificationService? notifications = null,
        BoltMate.Licensing.ILicenseGate? licenseGate = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(permissions);

        _settings = settings;
        _permissions = permissions;
        _licenseGate = licenseGate;
        _notifications = notifications;
        _isFirstRun = isFirstRun;
        _log = log ?? NullLogger.Instance;

        // Skip sign-in if the cached license already verifies.
        if (_licenseGate?.Current.IsEntitled == true)
            _currentPage = PageWelcome;

        BuildSteps();
        UpdateStepStates();

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

        // Notifications primer is informational. The OS owns the toggle;
        // we just surface its current state and let the user click through
        // to System Settings if they want to change it. Continue advances
        // regardless of state.
        NotificationsPrimerOpenSettingsCommand = ReactiveCommand.Create(() =>
        {
            _log.LogInformation("User tapped Open in System Settings on notifications primer");
            _notifications?.OpenOsSettings();
        });
        NotificationsPrimerContinueCommand = ReactiveCommand.Create(() =>
        {
            _log.LogInformation("User tapped Continue on notifications primer");
            SaveCheckpoint(PermissionNotifications);
            AdvanceToNextRequiredPermissionOrDone();
        });

        QuitCommand = ReactiveCommand.Create(() =>
        {
            _log.LogInformation("User tapped Quit BoltMate");
            _completedSuccessfully = true; // suppress Closing-handler quit-again
            QuitRequested?.Invoke(1);
        });
        DoneOpenCommand = ReactiveCommand.CreateFromTask(OnDoneOpenAsync);

        SignInCommand = ReactiveCommand.CreateFromTask(OnSignInAsync);
    }

    public ReactiveCommand<Unit, Unit> SignInCommand { get; }

    private string _signInStatus = string.Empty;
    public string SignInStatus
    {
        get => _signInStatus;
        set => this.RaiseAndSetIfChanged(ref _signInStatus, value);
    }

    private bool _signInBusy;
    public bool SignInBusy
    {
        get => _signInBusy;
        set => this.RaiseAndSetIfChanged(ref _signInBusy, value);
    }

    private async Task OnSignInAsync()
    {
        if (_licenseGate is null)
        {
            // Dev path — no license gate registered. Skip straight in.
            CurrentPage = PageWelcome;
            return;
        }
        SignInBusy = true;
        SignInStatus = "Opening browser…";
        try
        {
            var status = await _licenseGate.ActivateAsync().ConfigureAwait(true);
            if (status.IsEntitled)
            {
                _log.LogInformation("Sign-in completed: {State} tier={Tier}", status.State, status.Tier);
                CurrentPage = PageWelcome;
            }
            else
            {
                SignInStatus = "Sign-in didn't complete. Try again.";
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sign-in failed");
            SignInStatus = "Sign-in failed: " + ex.Message;
        }
        finally
        {
            SignInBusy = false;
        }
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
            PermissionNotifications   => PageNotificationsPrimer,
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
            PageNotificationsPrimer => _permissions.Notifications,
            _ => null,
        };
        if (permission is null) return null;

        // Skip the initial synchronous replay from IsGrantedChanged's
        // BehaviorSubject. Auto-advance is meant to handle "user clicked
        // Grant (or granted elsewhere) WHILE on the primer page" — not
        // "permission was already granted at first render." Without this
        // skip, opening a primer whose permission is already granted
        // (re-launch, fix-flow with no real change, multicast probe
        // optimistically returning Granted at boot) auto-advances within
        // milliseconds and the user never sees the page. Status line is
        // still refreshed via a separate observable so the page reflects
        // the current grant state immediately.
        permission.IsGrantedChanged.Subscribe(_ =>
        {
            if (_torndown || _completedSuccessfully) return;
            try { UpdateStatusLines(); } catch { /* swallow */ }
        });
        return permission.IsGrantedChanged
            .DistinctUntilChanged()
            .Skip(1)
            .Subscribe(granted =>
            {
                if (_torndown || _completedSuccessfully) return;
                if (!granted) return;
                try
                {
                    _log.LogInformation("Auto-advance: {Permission} granted while on {Page}", permission.Name, pageName);
                    SaveCheckpoint(permission.Name);
                    AdvanceToNextRequiredPermissionOrDone();
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
            // Show the primer only when the permission isn't actually
            // granted. Users with grants already in place (re-install,
            // prior dev runs, etc.) skip the screen — they don't need
            // to walk through a primer that drives a no-op grant. If
            // grants are missing, the primer's Grant button fires both
            // prompts (TCC Local Network + Application Firewall) inline.
            if (!_permissions.Network.IsGranted)
            {
                _log.LogInformation("Permission gate: network not granted — showing primer");
                _primersShownThisSession.Add(PermissionNetwork);
                ShowPage(PageNetworkPrimer);
                return;
            }
            SaveCheckpoint(PermissionNetwork);
        }

        if (OperatingSystem.IsMacOS())
        {
            if (!_permissions.InputMonitoring.IsGranted)
            {
                _log.LogInformation("Permission gate: input-monitoring not granted — showing primer (firstRun={First}, ckpt={Ckpt})",
                    _isFirstRun, _settings.InputMonitoringStepCompleted);
                _primersShownThisSession.Add(PermissionInputMonitoring);
                ShowPage(PageInputMonitoringPrimer);
                return;
            }
            SaveCheckpoint(PermissionInputMonitoring);
        }

        // Notifications primer — non-blocking + optional. Distinct gating
        // per platform because the OS-grant signal means different things:
        //   • Mac: UN center has a real NotDetermined state, so we only
        //     show the primer when not yet authorised. Granted-at-launch
        //     means the user already opted in on a prior install.
        //   • Win: AppNotificationManager.Setting defaults to Enabled the
        //     moment Register() runs — there is no "NotDetermined" we can
        //     observe. Gating on IsGranted would skip the primer on every
        //     fresh install, leaving the user unaware that the in-app
        //     toggle (AppEnabled pref) even exists. Gate instead on the
        //     wizard checkpoint: first run + step not yet completed.
        //
        // The CurrentPage check handles the "user is on the page, hasn't
        // acted yet, a spurious second Advance fired" case so we don't
        // skip them past. AND-gated on the checkpoint flag so that when
        // the user's own Allow / Not Now handler triggers Advance, we
        // proceed normally — those handlers save the checkpoint first,
        // so a true value means the user has acted.
        if (CurrentPage == PageNotificationsPrimer && !_settings.NotificationsStepCompleted)
        {
            // User is on the primer and hasn't acted. Wait for them.
            return;
        }

        // Notifications primer is informational on both platforms — status
        // pill + open-Settings + Continue. Show it once per first-run
        // regardless of current OS auth state so the user always sees the
        // page and learns where to manage it. Gating on IsGranted would
        // skip it on Mac when a prior install left UN center authorised.
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            if (_isFirstRun && !_settings.NotificationsStepCompleted
                && !_primersShownThisSession.Contains(PermissionNotifications))
            {
                _log.LogInformation("Permission gate: notifications primer not yet shown — showing");
                _primersShownThisSession.Add(PermissionNotifications);
                ShowPage(PageNotificationsPrimer);
                return;
            }
            SaveCheckpoint(PermissionNotifications);
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
        var notifGranted = _permissions.Notifications.IsGranted;
        if (notifGranted)
        {
            NotificationsPillText = "Enabled";
            NotificationsPillBackground = "#1A22C55E";
            NotificationsPillForeground = "#22C55E";
            NotificationsPillDot = "#22C55E";
            NotificationsStatusLine = "Notifications: enabled";
        }
        else
        {
            NotificationsPillText = "Disabled";
            NotificationsPillBackground = "#1A9CA3AF";
            NotificationsPillForeground = "#9CA3AF";
            NotificationsPillDot = "#9CA3AF";
            NotificationsStatusLine = "Notifications: not yet enabled";
        }
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
                case PermissionNotifications:           _settings.NotificationsStepCompleted = true; break;
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
        _log.LogInformation("User clicked Get started (autostart={Autostart})", AutostartChecked);
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
            if (granted)
            {
                // Explicit advance on successful grant — the WatchPermission
                // subscriber Skip(1)s the initial replay, so it'll only fire
                // on a denied→granted transition. When the permission was
                // already granted at button-press, GrantAsync returns true
                // synchronously with no state change → no emission to catch.
                // Driving the advance from here covers both cases (already
                // granted + just granted) without risk of double-advancing
                // because AdvanceToNextRequiredPermissionOrDone is idempotent
                // wrt the current page.
                SaveCheckpoint(permission.Name);
                AdvanceToNextRequiredPermissionOrDone();
            }
            else if (refusalPage is not null && CurrentPage != refusalPage)
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

    // ---- Step tracker ------------------------------------------------

    private void BuildSteps()
    {
        Steps.Clear();
        if (OperatingSystem.IsMacOS())
        {
            Steps.Add(new WizardStep(StepKeyWelcome,         "Welcome"));
            Steps.Add(new WizardStep(StepKeyLocalNetwork,    "Local Network"));
            Steps.Add(new WizardStep(StepKeyInputMonitoring, "Input Monitoring"));
            Steps.Add(new WizardStep(StepKeyNotifications,   "Notifications"));
            Steps.Add(new WizardStep(StepKeyDone,            "Done"));
        }
        else if (OperatingSystem.IsWindows())
        {
            Steps.Add(new WizardStep(StepKeyWelcome,       "Welcome"));
            Steps.Add(new WizardStep(StepKeyFirewall,      "Windows Firewall"));
            Steps.Add(new WizardStep(StepKeyNotifications, "Notifications"));
            Steps.Add(new WizardStep(StepKeyDone,          "Done"));
        }
        else
        {
            // Linux: the wizard short-circuits to the fast-path page; we
            // still render a single rail entry so the layout doesn't
            // collapse.
            Steps.Add(new WizardStep(StepKeyDone, "Linux"));
        }
    }

    /// <summary>
    /// Recomputes each step's <see cref="WizardStepState"/> and the
    /// "Step N of M" caption from the current page. Called every time
    /// <see cref="CurrentPage"/> changes (including the initial set in
    /// the ctor).
    /// </summary>
    private void UpdateStepStates()
    {
        if (Steps.Count == 0) return;
        var currentKey = MapPageToStepKey(CurrentPage);
        var activeIdx = -1;
        for (var i = 0; i < Steps.Count; i++)
            if (Steps[i].Key == currentKey) { activeIdx = i; break; }

        for (var i = 0; i < Steps.Count; i++)
        {
            var state = activeIdx < 0
                ? WizardStepState.Upcoming
                : i < activeIdx ? WizardStepState.Done
                : i == activeIdx ? WizardStepState.Active
                : WizardStepState.Upcoming;
            Steps[i].State = state;
        }

        // Caption shows the 1-based index of the active step, or stays
        // blank when nothing's active (e.g. Linux fallback page).
        StepCaption = activeIdx >= 0
            ? $"Step {activeIdx + 1} of {Steps.Count}"
            : "";
    }

    private static string MapPageToStepKey(string page) => page switch
    {
        PageWelcome                    => StepKeyWelcome,
        // Both primer and refusal share their permission's step.
        PageNetworkPrimer or PageNetworkRefusal
            => OperatingSystem.IsWindows() ? StepKeyFirewall : StepKeyLocalNetwork,
        PageInputMonitoringPrimer or PageInputMonitoringRefusal
            => StepKeyInputMonitoring,
        PageNotificationsPrimer        => StepKeyNotifications,
        PageDone                       => StepKeyDone,
        PageLinux                      => StepKeyDone,
        _                              => "",
    };

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
