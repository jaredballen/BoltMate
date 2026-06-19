using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;
using LogiPlusSwitcher.Hid.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogiPlusSwitcher.Core.Bolt;

/// <summary>
/// Watches USB for Bolt receivers attaching and detaching, opens a
/// <see cref="BoltReceiver"/> for each one, and disposes it cleanly when the
/// device disappears. Handles unplug→replug cycles and concurrent
/// multi-receiver setups (one Bolt per host, etc.).
/// </summary>
/// <remarks>
/// Polls the transport at <see cref="PollInterval"/> rather than using OS-level
/// hot-plug notifications. The poll is cheap (a single HID enumeration) and
/// avoids platform-specific dependencies. Bumps to OS notifications can be
/// layered in later without changing this type's public surface.
/// </remarks>
public sealed class ReceiverManager : IDisposable
{
    private readonly IReceiverTransport _transport;
    private readonly Func<BoltReceiverInfo, IReceiverConnection, BoltReceiver> _factory;
    private readonly ILogger<ReceiverManager> _logger;
    private readonly SourceCache<BoltReceiver, string> _receiversCache;
    private readonly Subject<Exception> _attachFailures = new();
    private readonly CompositeDisposable _disposables = new();
    private readonly object _refreshGate = new();
    private bool _disposed;

    /// <summary>How often to re-enumerate USB devices.</summary>
    public TimeSpan PollInterval { get; }

    /// <summary>Live cache of currently-attached Bolt receivers, keyed by HID path.</summary>
    public IObservableCache<BoltReceiver, string> Receivers { get; }

    /// <summary>Stream of attach failures (open threw — device went away mid-open, OS error).</summary>
    public IObservable<Exception> AttachFailures => _attachFailures.AsObservable();

    public ReceiverManager(
        IReceiverTransport transport,
        TimeSpan? pollInterval = null,
        Func<BoltReceiverInfo, IReceiverConnection, BoltReceiver>? receiverFactory = null,
        bool autoStart = true,
        ILoggerFactory? loggerFactory = null)
    {
        _transport = transport;
        var lf = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = lf.CreateLogger<ReceiverManager>();
        _factory = receiverFactory ?? ((info, conn) => new BoltReceiver(info, conn, logger: lf.CreateLogger<BoltReceiver>()));
        PollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _receiversCache = new SourceCache<BoltReceiver, string>(r => r.Info.Path);
        Receivers = _receiversCache.AsObservableCache();

        _disposables.Add(_receiversCache);
        _disposables.Add((IDisposable)Receivers);
        _disposables.Add(_attachFailures);
        _disposables.Add(Disposable.Create(DisposeAllReceivers));

        if (autoStart)
        {
            var timer = new Timer(_ => SafeRefresh(), null, TimeSpan.Zero, PollInterval);
            _disposables.Add(timer);
        }
    }

    /// <summary>
    /// Forces an immediate reconciliation. Useful for tests and for the
    /// initial pass when polling is disabled (<c>autoStart: false</c>).
    /// </summary>
    public void Refresh() => SafeRefresh();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposables.Dispose();
    }

    private void DisposeAllReceivers()
    {
        foreach (var receiver in _receiversCache.Items)
        {
            try { receiver.Dispose(); } catch { /* swallow */ }
        }
        _receiversCache.Clear();
    }

    private void SafeRefresh()
    {
        if (_disposed) return;
        if (!Monitor.TryEnter(_refreshGate))
            return; // a previous tick is still in flight; skip this one

        try
        {
            Refresh_NoLock();
        }
        finally
        {
            Monitor.Exit(_refreshGate);
        }
    }

    private void Refresh_NoLock()
    {
        var current = _transport.Enumerate();
        var currentPaths = new HashSet<string>(current.Select(i => i.Path));

        // Remove vanished receivers.
        var removed = _receiversCache.Items.Where(r => !currentPaths.Contains(r.Info.Path)).ToArray();
        foreach (var receiver in removed)
        {
            _logger.LogInformation("Receiver detached: serial {Serial}", receiver.Info.Serial);
            _receiversCache.Remove(receiver);
            try { receiver.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "Receiver dispose failed"); }
        }

        // Add new receivers.
        foreach (var info in current)
        {
            if (_receiversCache.Lookup(info.Path).HasValue)
                continue;

            try
            {
                var connection = _transport.Open(info);
                var receiver = _factory(info, connection);
                _receiversCache.AddOrUpdate(receiver);
                receiver.Start();
                _logger.LogInformation("Receiver attached: {Product} (serial {Serial})", info.ProductString, info.Serial);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Attach failed for {Path}", info.Path);
                _attachFailures.OnNext(ex);
            }
        }
    }
}
