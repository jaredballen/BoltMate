using LogiPlusSwitcher.Core.HidPp;

namespace LogiPlusSwitcher.Core.Hid;

/// <summary>
/// An open, non-exclusive connection to a Bolt receiver's HID++ management
/// interface. Reads run on a background pump and are surfaced via
/// <see cref="FrameReceived"/>; writes are synchronous fire-and-forget.
/// </summary>
public interface IReceiverConnection : IDisposable
{
    /// <summary>Raised on every successfully parsed inbound HID++ report.</summary>
    event EventHandler<HidPpFrame>? FrameReceived;

    /// <summary>Raised when the read pump throws (device disconnected, OS error).</summary>
    event EventHandler<Exception>? ReadError;

    /// <summary>Writes a HID++ frame to the receiver.</summary>
    void Write(HidPpFrame frame);

    /// <summary>Starts the background read pump. Idempotent.</summary>
    void Start();

    /// <summary>Stops the background read pump. Does not close the device.</summary>
    void Stop();
}
