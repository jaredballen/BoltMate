using LogiPlusSwitcher.Core.HidPp;

namespace LogiPlusSwitcher.Core.Hid;

/// <summary>
/// An open, non-exclusive connection to a Bolt receiver's HID++ management
/// interface. Inbound reports are surfaced as a hot <see cref="IObservable{T}"/>
/// of parsed frames; read-pump failures land on <see cref="ReadErrors"/>.
/// Writes are synchronous fire-and-forget.
/// </summary>
public interface IReceiverConnection : IDisposable
{
    /// <summary>Hot stream of every parsed inbound HID++ report.</summary>
    IObservable<HidPpFrame> InboundFrames { get; }

    /// <summary>Hot stream of read-pump exceptions (device disconnected, OS error).</summary>
    IObservable<Exception> ReadErrors { get; }

    /// <summary>Writes a HID++ frame to the receiver.</summary>
    void Write(HidPpFrame frame);

    /// <summary>Starts the background read pump. Idempotent.</summary>
    void Start();

    /// <summary>Stops the background read pump. Does not close the device.</summary>
    void Stop();
}
