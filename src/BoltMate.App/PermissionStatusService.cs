using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using BoltMate.Hid.IOKit;

namespace BoltMate.App;

/// <summary>
/// Live aggregate of the OS-level permissions BoltMate needs to operate:
///   • Local Network (mac + win)
///   • Input Monitoring (mac only — gates HID access via IOKit)
///
/// State is recomputed on every <see cref="Recheck"/> call and on a 5s
/// background timer; nothing is cached longer than that, because the user
/// can flip a TCC switch in System Settings at any time and we want to
/// reflect it without an app restart.
/// </summary>
public sealed class PermissionStatusService : IDisposable
{
    private readonly TimeSpan _pollInterval;
    private readonly Subject<PermissionStatus> _changes = new();
    private readonly CompositeDisposable _disposables = new();
    private PermissionStatus _last;
    private bool _started;

    public PermissionStatusService(TimeSpan? pollInterval = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        _last = Compute();
    }

    /// <summary>Current snapshot. Computed live (no stale cache beyond the in-memory snapshot).</summary>
    public PermissionStatus Current => _last;

    /// <summary>
    /// Stream of permission snapshots. Replays the current value to new
    /// subscribers, then forwards subsequent updates. Disposed on Dispose().
    /// </summary>
    public IObservable<PermissionStatus> Observe()
    {
        // BehaviorSubject-ish pattern: prepend current to subject stream.
        return Observable.Return(_last).Concat(_changes.AsObservable());
    }

    /// <summary>Begin the background polling timer. Idempotent.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        var tick = Observable
            .Interval(_pollInterval)
            .Subscribe(_ => Recheck());
        _disposables.Add(tick);
    }

    /// <summary>Force a recompute now. Pushes a change only if state actually moved.</summary>
    public void Recheck()
    {
        // Invalidate the NetworkPermission internal 10s cache so the user gets
        // a fresh probe — they almost certainly just toggled something in
        // System Settings if they hit Recheck.
        NetworkPermission.Invalidate();

        var next = Compute();
        if (next != _last)
        {
            _last = next;
            _changes.OnNext(next);
        }
    }

    public void Dispose()
    {
        _disposables.Dispose();
        _changes.OnCompleted();
        _changes.Dispose();
    }

    private static PermissionStatus Compute()
    {
        var net = NetworkPermission.Check();
        var im = InputMonitoringPermission.Check();

        var netState = net.Status switch
        {
            NetworkPermission.Status.Granted => PermissionState.Granted,
            NetworkPermission.Status.Denied  => PermissionState.Denied,
            _                                => PermissionState.Unknown,
        };

        var imState = im switch
        {
            InputMonitoringPermission.Status.Granted        => PermissionState.Granted,
            InputMonitoringPermission.Status.Denied         => PermissionState.Denied,
            InputMonitoringPermission.Status.Unknown        => PermissionState.Unknown,
            InputMonitoringPermission.Status.NotApplicable  => PermissionState.NotApplicable,
            _                                               => PermissionState.Unknown,
        };

        // Overall:
        //   AllGood   = every applicable permission Granted
        //   AnyDenied = at least one Denied
        //   Unknown   = otherwise (no Denied but at least one Unknown)
        var overall =
            (netState == PermissionState.Denied || imState == PermissionState.Denied) ? OverallStatus.AnyDenied :
            (netState == PermissionState.Granted &&
             (imState == PermissionState.Granted || imState == PermissionState.NotApplicable)) ? OverallStatus.AllGood :
                                                                                                  OverallStatus.Unknown;

        return new PermissionStatus(netState, imState, overall);
    }
}

/// <summary>Per-permission rollup state.</summary>
public enum PermissionState
{
    Granted,
    Denied,
    Unknown,
    /// <summary>Permission doesn't exist on this OS (e.g. Input Monitoring on Windows / Linux).</summary>
    NotApplicable,
}

/// <summary>Aggregate roll-up across every permission the app cares about.</summary>
public enum OverallStatus
{
    AllGood,
    AnyDenied,
    Unknown,
}

/// <summary>Snapshot returned by <see cref="PermissionStatusService"/>.</summary>
public readonly record struct PermissionStatus(
    PermissionState Network,
    PermissionState InputMonitoring,
    OverallStatus Overall);
