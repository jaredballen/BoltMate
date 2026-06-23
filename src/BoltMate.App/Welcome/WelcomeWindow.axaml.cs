using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BoltMate.App.Services;
using BoltMate.App.ViewModels;
using BoltMate.Core;
using BoltMate.Core.Permissions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.App.Welcome;

/// <summary>
/// First-run welcome wizard + on-demand "Fix permissions" entry. All state
/// + commands live in <see cref="WelcomeViewModel"/>; this code-behind only
/// does Avalonia-specific plumbing — XAML init, Closing intercept,
/// QuitApp() shutdown, and forwarding the VM's
/// <see cref="WelcomeViewModel.WelcomeCompleted"/> event up to App.
/// </summary>
public partial class WelcomeWindow : Window
{
    public const string PermissionNetwork = WelcomeViewModel.PermissionNetwork;
    public const string PermissionInputMonitoring = WelcomeViewModel.PermissionInputMonitoring;

    private readonly WelcomeViewModel _vm;
    private readonly bool _isFirstRun;
    private bool _quitting;

    /// <summary>VM exposed so callers can drive activation lifecycle.</summary>
    public WelcomeViewModel ViewModel => _vm;

    /// <summary>Fired once on the happy-path Open BoltMate press; App layer subscribes.</summary>
    public event Action? WelcomeCompleted;

    /// <summary>Designer-only ctor. Real usage goes through the parameterised ctor.</summary>
    public WelcomeWindow() : this(new AppSettings(), new PermissionsService(), isFirstRun: true, NullLogger.Instance) { }

    public WelcomeWindow(AppSettings settings, IPermissionsService permissions, bool isFirstRun = true, ILogger? log = null)
    {
        _isFirstRun = isFirstRun;
        _vm = new WelcomeViewModel(settings, permissions, isFirstRun, log);
        DataContext = _vm;
        InitializeComponent();

        _vm.QuitRequested += code => Dispatcher.UIThread.Post(() => QuitApp(code));
        _vm.WelcomeCompleted += () => WelcomeCompleted?.Invoke();
        _vm.CloseRequested += () => Dispatcher.UIThread.Post(Close);

        // Intercept manual close. First-run + non-happy-path dismiss = quit.
        // Fix-permissions mode dismiss = just close (rest of app already up).
        Closing += (_, _) =>
        {
            // Re-entry guard. QuitApp -> desktop.Shutdown fires Close on every
            // window, which re-enters this handler. Bail on second pass.
            if (_quitting || _vm.CompletedSuccessfully) return;
            _quitting = true;
            _vm.TeardownActivation();
            if (_isFirstRun)
            {
                log?.LogInformation("Welcome wizard dismissed without completion — quitting app");
                QuitApp();
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Kicks the VM into its initial state. Called by App after construction.</summary>
    public void RunFlow() => _vm.RunFlow();

    /// <summary>Jumps the VM to a specific primer. Called by the tray "Fix permissions…" item.</summary>
    public void OpenToPrimer(string permissionId) => _vm.OpenToPrimer(permissionId);

    private static void QuitApp(int exitCode = 0)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown(exitCode);
    }
}
