using System.Collections.Concurrent;
using LogiPlusSwitcher.Core.Hid;

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
    private readonly ConcurrentDictionary<string, BoltReceiver> _receivers = new();
    private readonly Func<BoltReceiverInfo, IReceiverConnection, BoltReceiver> _factory;
    private readonly Timer? _pollTimer;
    private readonly object _refreshGate = new();
    private bool _disposed;

    /// <summary>How often to re-enumerate USB devices.</summary>
    public TimeSpan PollInterval { get; }

    /// <summary>Currently-attached receivers.</summary>
    public IReadOnlyCollection<BoltReceiver> Receivers => _receivers.Values.ToList();

    /// <summary>Fires immediately after a new receiver is opened and started.</summary>
    public event EventHandler<BoltReceiver>? ReceiverAttached;

    /// <summary>Fires just before a vanished receiver is disposed.</summary>
    public event EventHandler<BoltReceiver>? ReceiverDetached;

    /// <summary>Fires when an attach attempt throws (e.g. device disappeared mid-open).</summary>
    public event EventHandler<Exception>? AttachFailed;

    public ReceiverManager(
        IReceiverTransport transport,
        TimeSpan? pollInterval = null,
        Func<BoltReceiverInfo, IReceiverConnection, BoltReceiver>? receiverFactory = null,
        bool autoStart = true)
    {
        _transport = transport;
        _factory = receiverFactory ?? ((info, conn) => new BoltReceiver(info, conn));
        PollInterval = pollInterval ?? TimeSpan.FromSeconds(2);

        if (autoStart)
            _pollTimer = new Timer(_ => SafeRefresh(), null, TimeSpan.Zero, PollInterval);
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

        _pollTimer?.Dispose();

        foreach (var receiver in _receivers.Values)
        {
            try { receiver.Dispose(); } catch { /* swallow */ }
        }
        _receivers.Clear();
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
        foreach (var (path, receiver) in _receivers.ToArray())
        {
            if (currentPaths.Contains(path))
                continue;
            if (!_receivers.TryRemove(path, out var removed))
                continue;
            try { ReceiverDetached?.Invoke(this, removed); } catch { /* swallow */ }
            try { removed.Dispose(); } catch { /* swallow */ }
        }

        // Add new receivers.
        foreach (var info in current)
        {
            if (_receivers.ContainsKey(info.Path))
                continue;

            try
            {
                var connection = _transport.Open(info);
                var receiver = _factory(info, connection);
                if (!_receivers.TryAdd(info.Path, receiver))
                {
                    receiver.Dispose();
                    continue;
                }
                receiver.Start();
                ReceiverAttached?.Invoke(this, receiver);
            }
            catch (Exception ex)
            {
                AttachFailed?.Invoke(this, ex);
            }
        }
    }
}
