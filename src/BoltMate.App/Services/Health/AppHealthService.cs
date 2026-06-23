using BoltMate.Core.Services;
using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using BoltMate.Core.Bolt;
using BoltMate.Core.Permissions;
using BoltMate.Core.Topology;
using DynamicData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.App.Services;

/// <summary>
/// App-wide health monitor. Watches three independent failure surfaces —
/// OS permissions (required ones), network topology (all transports
/// blocked), and "no Bolt receiver attached" — and surfaces an aggregated
/// alert via the tray icon + a one-shot OS notification per transition
/// into a bad state.
/// </summary>
/// <remarks>
/// One transition into a bad state = one notification. Subsequent re-ticks
/// while still in the bad state do NOT re-notify (tray badge persists as
/// the standing indicator). If the condition clears and later re-asserts,
/// a fresh notification is posted — that's the user-confirmed cadence:
/// "only re-notify if it resolved and came back."
///
/// Per-category debounce so transient flap doesn't trigger a notification:
/// permissions = immediate, network = 30s sustained, receiver = 5s
/// sustained. Per-category state is exposed on <see cref="Health"/> for the
/// UI / Status tab to render directly.
/// </remarks>
public sealed class AppHealthService : IAppHealthService
{
    private readonly IPermissionsService _permissions;
    private readonly IUdpTopologyService? _udp;
    private readonly IMdnsTcpChannel? _mdns;
    private readonly IReceiverManager _receivers;
    private readonly Func<string, string, bool> _postNotification;
    private readonly Action<OverallStatus> _setTrayStatus;
    private readonly ILogger _log;
    private readonly CompositeDisposable _disposables = new();

    private readonly CategoryTracker _permTracker = new("Permissions", TimeSpan.Zero);
    private readonly CategoryTracker _netTracker = new("Network", TimeSpan.FromSeconds(30));
    private readonly CategoryTracker _recvTracker = new("Receiver", TimeSpan.FromSeconds(5));

    private TransportHealth _udpHealth = TransportHealth.Unknown("");
    private TransportHealth _syncHealth = TransportHealth.Unknown("");

    private readonly BehaviorSubject<AppHealthSnapshot> _health;

    public IObservable<AppHealthSnapshot> Health => _health.AsObservable();
    public AppHealthSnapshot Current => _health.Value;

    public AppHealthService(
        IPermissionsService permissions,
        IUdpTopologyService? udp,
        IMdnsTcpChannel? mdns,
        IReceiverManager receivers,
        Func<string, string, bool> postNotification,
        Action<OverallStatus> setTrayStatus,
        ILogger<AppHealthService>? logger = null)
    {
        _permissions = permissions;
        _udp = udp;
        _mdns = mdns;
        _receivers = receivers;
        _postNotification = postNotification;
        _setTrayStatus = setTrayStatus;
        _log = logger ?? NullLogger<AppHealthService>.Instance;

        _health = new BehaviorSubject<AppHealthSnapshot>(AppHealthSnapshot.AllOk);

        // Watch the inputs. Each callback only flips the "raw bad" booleans
        // — debounce + notification happens inside Recompute, which the
        // dispatcher timer fires every second.
        _disposables.Add(_permissions.Network.IsGrantedChanged.Subscribe(_ => UpdatePermissionRaw()));
        _disposables.Add(_permissions.InputMonitoring.IsGrantedChanged.Subscribe(_ => UpdatePermissionRaw()));
        if (_udp is not null)
            _disposables.Add(_udp.UdpHealth.Subscribe(h => { _udpHealth = h; UpdateNetworkRaw(); }));
        if (_mdns is not null)
        {
            // SyncHealth is the rolled-up mDNS + TCP signal. We only watch
            // the combined view here — the per-protocol breakdown lives in
            // the logs (each Emit*Health method writes its own transition).
            _disposables.Add(_mdns.SyncHealth.Subscribe(h => { _syncHealth = h; UpdateNetworkRaw(); }));
        }
        _disposables.Add(_receivers.Receivers.CountChanged.Subscribe(_ => UpdateReceiverRaw()));

        // Seed the trackers from initial input values.
        UpdatePermissionRaw();
        UpdateNetworkRaw();
        UpdateReceiverRaw();

        // 1s dispatcher tick drives debounce + emit. UI thread so the tray
        // controller + notification calls don't need explicit marshalling.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Recompute();
        timer.Start();
        _disposables.Add(Disposable.Create(timer.Stop));

        Recompute();
    }

    public void Dispose() => _disposables.Dispose();

    // -----------------------------------------------------------------
    // Raw input updates — just mutate the tracker.RawBad flags. Debounce
    // happens in Recompute().
    // -----------------------------------------------------------------

    private void UpdatePermissionRaw()
    {
        // Network + HID are the two required permissions. Autostart is a
        // user preference; not having it on is not an alertable condition.
        // HID (Input Monitoring) is Mac-only — on Win/Linux the impl is
        // AlwaysGrantedPermission so its IsGranted is true.
        var networkDenied = !_permissions.Network.IsGranted;
        var hidDenied = !_permissions.InputMonitoring.IsGranted;
        _permTracker.RawBad = networkDenied || hidDenied;
        var reasons = (networkDenied, hidDenied) switch
        {
            (true, true) => "Local Network + Input Monitoring permissions denied",
            (true, false) => "Local Network permission denied",
            (false, true) => "Input Monitoring permission denied",
            _ => "all required permissions granted",
        };
        _permTracker.CurrentDetail = reasons;
    }

    private void UpdateNetworkRaw()
    {
        // No topology service wired (user has cross-machine sync disabled)
        // → not an alertable condition. Topology being off is a choice,
        // not a failure.
        if (_udp is null)
        {
            _netTracker.RawBad = false;
            _netTracker.CurrentDetail = "cross-machine sync disabled in settings";
            return;
        }

        // PermissionDenied wins — there's no point telling the user
        // "all transports blocked" when the real issue is "OS won't let
        // us bind." Permission-tracker covers the OS grant itself; the
        // network alert here would just duplicate it. Treat the network
        // category as healthy in that case so we don't double-toast.
        var anyPermDenied = _udpHealth.State is TransportState.PermissionDenied
                            || _syncHealth.State is TransportState.PermissionDenied;
        if (anyPermDenied)
        {
            _netTracker.RawBad = false;
            _netTracker.CurrentDetail = "Local Network permission not granted — see Permissions alert";
            return;
        }

        // Two transports surfaced to the user: UDP multicast and the
        // combined Bonjour-mDNS + TCP "reliable sync" path. Network is
        // alertable only when BOTH are Blocked — if either is reachable
        // peers can still get the fan-out announcements. The per-
        // protocol signals (mDNS vs TCP) stay in the logs for diagnosis,
        // they're just rolled up here for the user-facing decision.
        var bothBlocked = _udpHealth.State is TransportState.Blocked
                          && _syncHealth.State is TransportState.Blocked;
        _netTracker.RawBad = bothBlocked;
        _netTracker.CurrentDetail = bothBlocked
            ? $"both transports blocked — UDP multicast: {_udpHealth.DetailMessage} || Bonjour sync: {_syncHealth.DetailMessage}"
            : "at least one transport reachable";
    }

    private void UpdateReceiverRaw()
    {
        var count = _receivers.Receivers.Count;
        _recvTracker.RawBad = count == 0;
        _recvTracker.CurrentDetail = count == 0
            ? "no Bolt receiver attached — plug one in to enable host-switch fan-out"
            : $"{count} receiver{(count == 1 ? "" : "s")} attached";
    }

    // -----------------------------------------------------------------
    // Debounce + transition handling.
    // -----------------------------------------------------------------

    private void Recompute()
    {
        var now = DateTimeOffset.UtcNow;
        TickCategory(_permTracker, now);
        TickCategory(_netTracker, now);
        TickCategory(_recvTracker, now);

        var snapshot = new AppHealthSnapshot(
            Permissions: _permTracker.Alerting ? new CategoryAlert(_permTracker.Name, _permTracker.CurrentDetail) : null,
            Network: _netTracker.Alerting ? new CategoryAlert(_netTracker.Name, _netTracker.CurrentDetail) : null,
            Receiver: _recvTracker.Alerting ? new CategoryAlert(_recvTracker.Name, _recvTracker.CurrentDetail) : null);

        // Re-publish even when the same shape — UI may want to refresh
        // tooltips. The distinct check is cheap inside subscribers if they
        // care.
        _health.OnNext(snapshot);

        // Tray badge: any alert = AnyDenied (reuses the existing enum so
        // we don't churn TrayIconStatusController's wiring). The actual
        // tooltip-detail wiring is the UI's job.
        _setTrayStatus(snapshot.IsAlerting ? OverallStatus.AnyDenied : OverallStatus.AllGood);
    }

    private void TickCategory(CategoryTracker t, DateTimeOffset now)
    {
        if (t.RawBad)
        {
            if (t.BadSince is null) t.BadSince = now;
            var sustained = now - t.BadSince.Value;
            if (sustained >= t.DebounceTime && !t.Alerting)
            {
                t.Alerting = true;
                _log.LogWarning("Health alert: {Category} — {Detail}", t.Name, t.CurrentDetail);
                _postNotification(
                    $"BoltMate · {t.Name} issue",
                    t.CurrentDetail);
            }
        }
        else
        {
            if (t.Alerting)
            {
                _log.LogInformation("Health cleared: {Category} — {Detail}", t.Name, t.CurrentDetail);
                t.Alerting = false;
            }
            t.BadSince = null;
        }
    }

    private sealed class CategoryTracker
    {
        public string Name { get; }
        public TimeSpan DebounceTime { get; }
        public bool RawBad;
        public DateTimeOffset? BadSince;
        public bool Alerting;
        public string CurrentDetail = "";

        public CategoryTracker(string name, TimeSpan debounce)
        {
            Name = name;
            DebounceTime = debounce;
        }
    }
}

/// <summary>Aggregate health snapshot. Null categories are healthy.</summary>
public sealed record AppHealthSnapshot(
    CategoryAlert? Permissions,
    CategoryAlert? Network,
    CategoryAlert? Receiver)
{
    public bool IsAlerting => Permissions is not null || Network is not null || Receiver is not null;
    public static AppHealthSnapshot AllOk { get; } = new(null, null, null);
}

public sealed record CategoryAlert(string Category, string Detail);
