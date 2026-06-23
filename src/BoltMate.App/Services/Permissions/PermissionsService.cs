using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using BoltMate.Core.Permissions;
using BoltMate.Hid.IOKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.App.Services;

/// <summary>
/// App-wide OS permission tracker. Owns one polling timer that re-probes the
/// three permissions every <see cref="PollInterval"/> and pushes deltas to
/// each <see cref="IPermission.IsGrantedChanged"/>. Anyone (welcome wizard,
/// tray menu, settings status tab) can subscribe.
/// </summary>
public sealed class PermissionsService : IPermissionsService
{
    // Backstop poll. macOS exposes no event for TCC state changes (no
    // NSNotification, no IOKit notify, nothing in CFNotificationCenter)
    // so polling is unavoidable — but we lean on reactive triggers
    // (network address change, OS wake) for fast turnaround and let the
    // backstop catch anything those missed. 1 Hz is cheap (a few syscalls
    // per tick) and gives a comfortable upper bound on staleness.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _timer;
    private readonly ILogger _log;
    private readonly PermissionBase _network;
    private readonly PermissionBase _inputMonitoring;
    private readonly PermissionBase _autostart;
    private bool _disposed;

    public IPermission Network => _network;
    public IPermission InputMonitoring => _inputMonitoring;
    public IPermission Autostart => _autostart;

    public PermissionsService(ILoggerFactory? loggerFactory = null)
    {
        var lf = loggerFactory ?? NullLoggerFactory.Instance;
        _log = lf.CreateLogger<PermissionsService>();

        // Pipe app-scoped loggers into the static permission helpers so
        // every IOHIDCheckAccess / SocketException / firewall-rule decision
        // lands in the Serilog file. Default is NullLogger; set once.
        NetworkPermission.Log = lf.CreateLogger(typeof(NetworkPermission).FullName!);
        InputMonitoringPermission.Log = lf.CreateLogger(typeof(InputMonitoringPermission).FullName!);

        _network = new NetworkPermissionImpl(lf.CreateLogger<NetworkPermissionImpl>());
        _inputMonitoring = OperatingSystem.IsMacOS()
            ? new InputMonitoringPermissionImpl(lf.CreateLogger<InputMonitoringPermissionImpl>())
            : new AlwaysGrantedPermission("input-monitoring");
        _autostart = new AutostartPermissionImpl(lf.CreateLogger<AutostartPermissionImpl>());

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += OnTick;
        _timer.Start();

        // Reactive triggers — fire an immediate Refresh on real-world
        // events that often coincide with permission state changes. Saves
        // the user from waiting a full poll interval after closing System
        // Settings or waking the machine.
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                MacWakeNotifier.WakeFromSleep += OnWakeFromSleep;
                MacWakeNotifier.Start(lf.CreateLogger(typeof(MacWakeNotifier).FullName!));
            }
            catch (Exception ex) { _log.LogDebug(ex, "MacWakeNotifier.Start failed — wake reactive trigger disabled"); }
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        _log.LogDebug("NetworkAddressChanged → immediate Refresh");
        Dispatcher.UIThread.Post(() => { try { Refresh(); } catch { } });
    }

    private void OnNetworkAvailabilityChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        _log.LogDebug("NetworkAvailabilityChanged → immediate Refresh");
        Dispatcher.UIThread.Post(() => { try { Refresh(); } catch { } });
    }

    private void OnWakeFromSleep()
    {
        _log.LogDebug("Wake from sleep → immediate Refresh");
        Dispatcher.UIThread.Post(() => { try { Refresh(); } catch { } });
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Tick may fire one more time after Stop() if it was already queued
        // on the dispatcher. Disposed subjects would throw on OnNext —
        // catch everything so the dispatcher loop doesn't tear the process
        // down during shutdown.
        try { Refresh(); }
        catch (Exception ex) { _log.LogWarning(ex, "Refresh swallowed during shutdown race"); }
    }

    public void Refresh()
    {
        if (_disposed) return;
        _network.PollAndPublish();
        _inputMonitoring.PollAndPublish();
        _autostart.PollAndPublish();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        if (OperatingSystem.IsMacOS())
        {
            try { MacWakeNotifier.WakeFromSleep -= OnWakeFromSleep; MacWakeNotifier.Stop(); } catch { }
        }
        _network.Dispose();
        _inputMonitoring.Dispose();
        _autostart.Dispose();
    }

    // ====================================================================
    // Per-permission implementations
    // ====================================================================

    /// <summary>Tri-state probe outcome — distinguishes "definitely no" from "OS didn't answer."</summary>
    internal enum ProbeStatus { Granted, Denied, Unknown }

    internal abstract class PermissionBase : IPermission, IDisposable
    {
        private readonly BehaviorSubject<bool> _subject;
        protected readonly ILogger Log;
        private bool _disposed;
        // Once true, an Unknown probe outcome will NOT flip IsGranted back
        // to false — only an explicit Denied will. Acknowledged by the
        // transport layer when a HID device-open succeeds; that's
        // empirical proof the OS grant is still in place even when
        // IOHIDCheckAccess has gone stale and reports Unknown.
        private bool _externallyAcknowledged;

        protected PermissionBase(string name, ILogger log)
        {
            Name = name;
            Log = log;
            _subject = new BehaviorSubject<bool>(false);
            try { _subject.OnNext(ProbeOs() is ProbeStatus.Granted); }
            catch (Exception ex) { Log.LogWarning(ex, "Initial probe failed for {Name}", name); }
        }

        public string Name { get; }
        public bool IsGranted => _subject.Value;
        public IObservable<bool> IsGrantedChanged => _subject.AsObservable();
        public abstract bool CanRevoke { get; }

        /// <summary>
        /// Probe the OS for current grant state. Returns
        /// <see cref="ProbeStatus.Unknown"/> when the OS won't commit
        /// either way (TCC cache miss, IOKit reports Unknown, etc.) so
        /// the base class can decide whether to downgrade IsGranted.
        /// </summary>
        protected abstract ProbeStatus ProbeOs();

        public void AcknowledgeExternalGrant()
        {
            if (_disposed) return;
            _externallyAcknowledged = true;
            try
            {
                if (!_subject.Value)
                {
                    Log.LogInformation("Permission {Name} → True (external acknowledgement)", Name);
                    _subject.OnNext(true);
                }
            }
            catch (ObjectDisposedException) { /* shutdown race */ }
        }

        /// <summary>
        /// Dispatch the OS-side action that should drive the permission
        /// toward <paramref name="target"/>. Implementations should issue
        /// prompts / open Settings panes / call launchctl, then return —
        /// the base class handles waiting for <see cref="IsGrantedChanged"/>
        /// to flip.
        /// </summary>
        protected abstract Task DispatchSetGrantedAsync(bool target, CancellationToken ct);

        public Task<bool> GrantAsync(CancellationToken ct = default) => DriveAsync(true, ct);

        public Task<bool> RevokeAsync(CancellationToken ct = default)
        {
            if (!CanRevoke)
            {
                Log.LogDebug("RevokeAsync({Name}) — CanRevoke=false, returning false", Name);
                return Task.FromResult(false);
            }
            return DriveAsync(false, ct);
        }

        private async Task<bool> DriveAsync(bool target, CancellationToken ct)
        {
            if (IsGranted == target) return true;

            try { await DispatchSetGrantedAsync(target, ct); }
            catch (OperationCanceledException) { return IsGranted == target; }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "DispatchSetGrantedAsync({Target}) threw for {Name}", target, Name);
            }

            if (IsGranted == target) return true;

            try
            {
                await _subject.AsObservable()
                    .FirstAsync(g => g == target)
                    .ToTask(ct);
                return true;
            }
            catch (OperationCanceledException)
            {
                return IsGranted == target;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // Teardown race: the BehaviorSubject can be disposed
                // mid-await when macOS forces an HID-grant relaunch — the
                // observable then completes without emitting target,
                // FirstAsync throws InvalidOperationException. Treat as
                // "best-effort, ended early."
                Log.LogDebug(ex, "DriveAsync teardown race for {Name}", Name);
                return IsGranted == target;
            }
        }

        public void PollAndPublish()
        {
            if (_disposed) return;
            ProbeStatus status;
            try { status = ProbeOs(); }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Probe threw for {Name}", Name);
                return;
            }

            // Resolve the published bool. Unknown is sticky once we've
            // either seen Granted or had an external acknowledgement —
            // macOS TCC's per-process cache can flap to Unknown after
            // hours of running and we don't want a phantom downgrade.
            bool next = status switch
            {
                ProbeStatus.Granted => true,
                ProbeStatus.Denied  => false,
                _                   => _externallyAcknowledged || _subject.Value,
            };

            try
            {
                if (next != _subject.Value)
                {
                    Log.LogInformation("Permission {Name} → {Granted} (probe={Probe})", Name, next, status);
                    _subject.OnNext(next);
                }
            }
            catch (ObjectDisposedException) { /* shutdown race — swallow */ }
            catch (Exception ex) { Log.LogWarning(ex, "PollAndPublish OnNext threw for {Name}", Name); }
        }

        public void Dispose()
        {
            _disposed = true;
            try { _subject.Dispose(); } catch { /* idempotent */ }
        }
    }

    internal sealed class NetworkPermissionImpl : PermissionBase
    {
        public NetworkPermissionImpl(ILogger log) : base("network", log) { }
        public override bool CanRevoke => false;

        protected override ProbeStatus ProbeOs()
        {
            NetworkPermission.Invalidate();
            return NetworkPermission.Check().Status switch
            {
                NetworkPermission.Status.Granted => ProbeStatus.Granted,
                NetworkPermission.Status.Denied  => ProbeStatus.Denied,
                _                                => ProbeStatus.Unknown,
            };
        }

        protected override Task DispatchSetGrantedAsync(bool target, CancellationToken ct)
        {
            if (!target) return Task.CompletedTask; // CanRevoke=false; never reaches here, but guard.

            NetworkPermission.Invalidate();
            var pre = NetworkPermission.Check();
            if (pre.Status is NetworkPermission.Status.Granted) return Task.CompletedTask;

            // Mac vs Win semantics for "Denied" differ:
            //   • Mac: Denied means the user explicitly toggled off in TCC.
            //     Request() can't re-prompt; only System Settings can fix.
            //   • Win: Denied just means "no Allow firewall rule yet" (or a
            //     Block rule that RequestWindows knows how to remove + retry).
            //     The Request flow IS the right path — it binds an inbound
            //     listener to trigger the Defender "Allow access" prompt.
            // Routing both to OpenSystemSettings would dump Win users into
            // the full firewall control panel instead of the simple prompt.
            if (OperatingSystem.IsMacOS() && pre.Status is NetworkPermission.Status.Denied)
            {
                Log.LogInformation("Mac: Local Network already denied — opening System Settings");
                NetworkPermission.OpenSystemSettings();
                return Task.CompletedTask;
            }

            Log.LogInformation("Network not Granted (status={Status}) — dispatching Request", pre.Status);
            return Task.Run(() => NetworkPermission.Request(), ct);
        }
    }

    internal sealed class InputMonitoringPermissionImpl : PermissionBase
    {
        public InputMonitoringPermissionImpl(ILogger log) : base("input-monitoring", log) { }
        public override bool CanRevoke => false;

        protected override ProbeStatus ProbeOs()
            => InputMonitoringPermission.Check() switch
            {
                InputMonitoringPermission.Status.Granted => ProbeStatus.Granted,
                InputMonitoringPermission.Status.Denied  => ProbeStatus.Denied,
                _                                        => ProbeStatus.Unknown,
            };

        protected override async Task DispatchSetGrantedAsync(bool target, CancellationToken ct)
        {
            if (!target) return; // CanRevoke=false

            var current = InputMonitoringPermission.Check();
            if (current == InputMonitoringPermission.Status.Granted) return;

            if (current == InputMonitoringPermission.Status.Denied)
            {
                Log.LogInformation("HID already denied — opening System Settings");
                InputMonitoringPermission.OpenSystemSettings();
                return;
            }

            MacActivationPolicy.ShowDockIcon();
            await Task.Delay(100, ct);
            Log.LogInformation("HID undecided — issuing IOHIDRequestAccess");
            await Task.Run(() => InputMonitoringPermission.Request(), ct);
        }
    }

    internal sealed class AutostartPermissionImpl : PermissionBase
    {
        public AutostartPermissionImpl(ILogger log) : base("autostart", log) { }
        public override bool CanRevoke => true;

        // Mirror System Settings → Login Items semantics: the plist on disk
        // is install-time concern; runtime "is autostart on?" = loaded state.
        // Boolean OS query, no Unknown state possible.
        protected override ProbeStatus ProbeOs() =>
            AppAutostart.IsLoaded() ? ProbeStatus.Granted : ProbeStatus.Denied;

        protected override async Task DispatchSetGrantedAsync(bool target, CancellationToken ct)
        {
            if (target)
            {
                if (!AppAutostart.CanRegister())
                {
                    Log.LogWarning("Autostart not registrable in current process layout");
                    return;
                }
                // AppAutostart.Install shells out to launchctl / reg.exe and
                // can take tens of ms — push it off the UI thread so the
                // welcome wizard stays responsive while it runs.
                var result = await Task.Run(AppAutostart.Install, ct);
                Log.LogInformation("Autostart install: success={Success} message={Message}",
                    result.Success, result.Message);
            }
            else
            {
                var result = await Task.Run(AppAutostart.Disable, ct);
                Log.LogInformation("Autostart disable: success={Success} message={Message}",
                    result.Success, result.Message);
            }
        }
    }

    /// <summary>Stand-in for permissions that don't exist on the current platform (e.g. HID on Windows).</summary>
    internal sealed class AlwaysGrantedPermission : PermissionBase
    {
        public AlwaysGrantedPermission(string name) : base(name, NullLogger.Instance) { }
        public override bool CanRevoke => false;
        protected override ProbeStatus ProbeOs() => ProbeStatus.Granted;
        protected override Task DispatchSetGrantedAsync(bool target, CancellationToken ct) => Task.CompletedTask;
    }
}
