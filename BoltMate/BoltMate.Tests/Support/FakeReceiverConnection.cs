using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using BoltMate.Hid.Abstractions;
using BoltMate.Core.HidPp;

namespace BoltMate.Tests.Support;

/// <summary>
/// Test double for <see cref="IReceiverConnection"/>. Captures every outgoing
/// frame and lets the test push synthetic inbound frames.
/// </summary>
public sealed class FakeReceiverConnection : IReceiverConnection
{
    private readonly Subject<HidPpFrame> _frames = new();
    private readonly Subject<Exception> _readErrors = new();
    private readonly Subject<HidPpFrame> _writes = new();
    private readonly CompositeDisposable _disposables = new();

    public ConcurrentQueue<HidPpFrame> Writes { get; } = new();

    public IObservable<HidPpFrame> InboundFrames => _frames.AsObservable();
    public IObservable<Exception> ReadErrors => _readErrors.AsObservable();

    /// <summary>Stream of writes the system-under-test issued — handy for tests that auto-respond.</summary>
    public IObservable<HidPpFrame> WriteStream => _writes.AsObservable();

    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }

    public FakeReceiverConnection()
    {
        _disposables.Add(_frames);
        _disposables.Add(_readErrors);
        _disposables.Add(_writes);
    }

    public void Write(HidPpFrame frame)
    {
        Writes.Enqueue(frame);
        _writes.OnNext(frame);
    }

    public void Start() => Started = true;
    public void Stop() => Stopped = true;

    public void Dispose()
    {
        Disposed = true;
        _disposables.Dispose();
    }

    /// <summary>Push a synthetic inbound frame as if the device sent it.</summary>
    public void Inject(HidPpFrame frame) => _frames.OnNext(frame);

    /// <summary>Push a synthetic read error.</summary>
    public void InjectReadError(Exception ex) => _readErrors.OnNext(ex);

    /// <summary>
    /// Convenience: subscribe an auto-responder to every write. Returned
    /// disposable unsubscribes.
    /// </summary>
    public IDisposable RespondWith(Action<HidPpFrame> responder) =>
        WriteStream.Subscribe(responder);
}
