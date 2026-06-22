using ReactiveUI;

namespace BoltMate.App.ViewModels;

/// <summary>
/// View-model for <see cref="Welcome.WelcomeWindow"/>. Holds bindable
/// state for the wizard (current page, per-page status lines, button
/// enabled flags, autostart toggle). Permission grant flow + close
/// interception + state-machine transitions stay in the window code-behind
/// because they touch lifecycle-sensitive paths (macOS HID-grant relaunch
/// race, single-instance teardown, CancellationTokenSource around Closing).
/// </summary>
/// <remarks>
/// This is a deliberate "partial MVVM" — bindable state moves to the VM so
/// a redesigned XAML can re-style layouts and labels without touching the
/// fragile permission/lifecycle code; click handlers update VM properties
/// from code-behind. When the welcome flow gets a redesign, the state
/// machine can be lifted entirely (the memory note flagged the wizard for
/// rework anyway) — at that point the VM expands and code-behind shrinks.
/// </remarks>
public sealed class WelcomeViewModel : ViewModelBase
{
    public const string PageWelcome = "PageWelcome";
    public const string PageNetworkPrimer = "PageNetworkPrimer";
    public const string PageNetworkRefusal = "PageNetworkRefusal";
    public const string PageInputMonitoringPrimer = "PageInputMonitoringPrimer";
    public const string PageInputMonitoringRefusal = "PageInputMonitoringRefusal";
    public const string PageDone = "PageDone";
    public const string PageLinux = "PageLinux";

    private string _currentPage = PageWelcome;
    public string CurrentPage
    {
        get => _currentPage;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentPage, value);
            // Derived per-page visibility flags raise their own changes so
            // {Binding ShowXxx} on each page's IsVisible updates atomically.
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
}
