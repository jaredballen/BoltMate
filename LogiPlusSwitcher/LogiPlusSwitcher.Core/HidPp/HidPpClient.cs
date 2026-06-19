using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using LogiPlusSwitcher.Core.Hid;

namespace LogiPlusSwitcher.Core.HidPp;

/// <summary>
/// Layers request/reply correlation and notification routing on top of a raw
/// <see cref="IReceiverConnection"/>. Outbound requests are stamped with our
/// software id (<see cref="HidPpConstants.OurSwId"/>); replies are matched
/// by (deviceIndex, function|swid byte). Frames whose sw_id is not ours are
/// surfaced on <see cref="Notifications"/>.
/// </summary>
/// <remarks>
/// Requests to a single device are serialised by a per-slot semaphore so that
/// in-flight (deviceIndex, function|swid) keys are always unique.
/// </remarks>
public sealed class HidPpClient : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(500);

    private readonly IReceiverConnection _connection;
    private readonly ConcurrentDictionary<byte, SemaphoreSlim> _deviceLocks = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<HidPpFrame>> _pending = new();
    private readonly Subject<HidPpFrame> _notifications = new();
    private readonly CompositeDisposable _disposables = new();
    private readonly byte _swId;
    private bool _disposed;

    /// <summary>Hot stream of inbound frames whose sw_id is not ours (device notifications + foreign writes).</summary>
    public IObservable<HidPpFrame> Notifications => _notifications.AsObservable();

    /// <summary>The connection this client wraps.</summary>
    public IReceiverConnection Connection => _connection;

    /// <summary>The software identifier this client stamps into outgoing requests.</summary>
    public byte SwId => _swId;

    public HidPpClient(IReceiverConnection connection, byte swId = HidPpConstants.OurSwId)
    {
        if (swId is 0 or > 0x0F)
            throw new ArgumentOutOfRangeException(nameof(swId), swId, "sw_id must be 1..15 (0 is reserved for device notifications).");

        _connection = connection;
        _swId = swId;

        _disposables.Add(_connection.InboundFrames.Subscribe(OnFrame));
        _disposables.Add(_notifications);
        _disposables.Add(Disposable.Create(() =>
        {
            foreach (var kvp in _pending)
                kvp.Value.TrySetCanceled();
            _pending.Clear();

            foreach (var sem in _deviceLocks.Values)
                sem.Dispose();
            _deviceLocks.Clear();
        }));
    }

    /// <summary>
    /// Sends a HID++ 2.0 request and awaits the matching reply. Throws
    /// <see cref="HidPpException"/> if the device returns an error reply or no
    /// reply arrives within <paramref name="timeout"/>.
    /// </summary>
    public async Task<HidPpFrame> RequestAsync(
        byte deviceIndex,
        byte featureIndex,
        int function,
        ReadOnlyMemory<byte> parameters = default,
        bool useLongReport = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var gate = _deviceLocks.GetOrAdd(deviceIndex, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var frame = useLongReport
                ? HidPpFrame.Long(deviceIndex, featureIndex, function, _swId, parameters.Span)
                : HidPpFrame.Short(deviceIndex, featureIndex, function, _swId, parameters.Span);

            var key = MakeKey(deviceIndex, frame.FunctionAndSwId);
            var tcs = new TaskCompletionSource<HidPpFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(key, tcs))
                throw new InvalidOperationException("Duplicate pending request key — concurrent dispatch was not serialised correctly.");

            try
            {
                _connection.Write(frame);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout ?? DefaultTimeout);
                using var registration = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));

                var reply = await tcs.Task.ConfigureAwait(false);

                if (reply.FeatureIndex == 0x8F)
                {
                    var errorCode = reply.Parameters.Span.Length > 1
                        ? (HidPpErrorCode)reply.Parameters.Span[1]
                        : HidPpErrorCode.Unknown;
                    throw new HidPpException(deviceIndex, featureIndex, function, errorCode);
                }

                return reply;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HidPpException(deviceIndex, featureIndex, function,
                    $"No reply within {(timeout ?? DefaultTimeout).TotalMilliseconds:F0} ms.");
            }
            finally
            {
                _pending.TryRemove(key, out _);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Sends a fire-and-forget HID++ frame (receiver register writes, CHANGE_HOST
    /// setCurrentHost, etc.) that don't yield a reply we care about.
    /// </summary>
    public void SendOneWay(HidPpFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _connection.Write(frame);
    }

    /// <summary>
    /// Builds a HID++ 2.0 long request stamped with our sw_id and writes it
    /// without waiting for a reply.
    /// </summary>
    public void SendLongOneWay(byte deviceIndex, byte featureIndex, int function, ReadOnlySpan<byte> parameters = default)
    {
        var frame = HidPpFrame.Long(deviceIndex, featureIndex, function, _swId, parameters);
        SendOneWay(frame);
    }

    /// <summary>
    /// Builds a HID++ 2.0 short request stamped with our sw_id and writes it
    /// without waiting for a reply.
    /// </summary>
    public void SendShortOneWay(byte deviceIndex, byte featureIndex, int function, ReadOnlySpan<byte> parameters = default)
    {
        var frame = HidPpFrame.Short(deviceIndex, featureIndex, function, _swId, parameters);
        SendOneWay(frame);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposables.Dispose();
    }

    private void OnFrame(HidPpFrame frame)
    {
        // Error replies use feature_index 0x8F but preserve the original fn|swid byte.
        var key = MakeKey(frame.DeviceIndex, frame.FunctionAndSwId);

        if (frame.SwId == _swId && _pending.TryRemove(key, out var tcs))
        {
            tcs.TrySetResult(frame);
            return;
        }

        // Anything else (device-originated notifications swid=0, Logi Options+ writes
        // with their swid, etc.) is a notification.
        _notifications.OnNext(frame);
    }

    private static int MakeKey(byte deviceIndex, byte fnSwIdByte) =>
        (deviceIndex << 8) | fnSwIdByte;
}
