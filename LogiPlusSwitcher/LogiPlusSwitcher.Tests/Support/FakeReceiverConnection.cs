using System.Collections.Concurrent;
using LogiPlusSwitcher.Core.Hid;
using LogiPlusSwitcher.Core.HidPp;

namespace LogiPlusSwitcher.Tests.Support;

/// <summary>
/// Test double for <see cref="IReceiverConnection"/>. Captures every outgoing
/// frame and lets the test push synthetic inbound frames.
/// </summary>
public sealed class FakeReceiverConnection : IReceiverConnection
{
    public ConcurrentQueue<HidPpFrame> Writes { get; } = new();

    public event EventHandler<HidPpFrame>? FrameReceived;
    public event EventHandler<Exception>? ReadError;

    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }

    public void Write(HidPpFrame frame) => Writes.Enqueue(frame);

    public void Start() => Started = true;
    public void Stop() => Stopped = true;

    public void Dispose()
    {
        Disposed = true;
    }

    /// <summary>Push a synthetic inbound frame as if the device sent it.</summary>
    public void Inject(HidPpFrame frame) => FrameReceived?.Invoke(this, frame);

    /// <summary>Push a synthetic read error.</summary>
    public void InjectReadError(Exception ex) => ReadError?.Invoke(this, ex);

    /// <summary>Tap a function over each write as it happens (e.g. auto-respond).</summary>
    public event Action<HidPpFrame>? OnWrite;

    void IReceiverConnection.Write(HidPpFrame frame)
    {
        Writes.Enqueue(frame);
        OnWrite?.Invoke(frame);
    }
}
