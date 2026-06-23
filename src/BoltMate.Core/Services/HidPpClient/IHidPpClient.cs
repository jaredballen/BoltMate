using BoltMate.Core.HidPp;
using BoltMate.Hid.Abstractions;

namespace BoltMate.Core.Services;

/// <summary>
/// HID++ 1.0 / 2.0 request-response client over a single receiver
/// connection. Tracks in-flight (deviceIndex, feature, function|swid)
/// keys and correlates replies; surfaces unmatched frames as notifications.
/// </summary>
public interface IHidPpClient : IDisposable
{
    /// <summary>Stream of inbound frames that aren't responses to in-flight requests.</summary>
    IObservable<HidPpFrame> Notifications { get; }

    /// <summary>Underlying receiver connection — escape hatch for raw frame I/O.</summary>
    IReceiverConnection Connection { get; }

    /// <summary>Software-id this client tags outgoing frames with so we can filter our own echoes.</summary>
    byte SwId { get; }

    /// <summary>
    /// Sends an HID++ 2.0 request and awaits the matching reply. Throws
    /// <see cref="HidPpException"/> on timeout or device-returned error code.
    /// </summary>
    Task<HidPpFrame> RequestAsync(
        byte deviceIndex,
        byte featureIndex,
        int function,
        ReadOnlyMemory<byte> parameters = default,
        bool useLongReport = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a frame with no expectation of reply.</summary>
    void SendOneWay(HidPpFrame frame);

    /// <summary>Convenience: long-report fire-and-forget.</summary>
    void SendLongOneWay(byte deviceIndex, byte featureIndex, int function, ReadOnlySpan<byte> parameters = default);

    /// <summary>Convenience: short-report fire-and-forget.</summary>
    void SendShortOneWay(byte deviceIndex, byte featureIndex, int function, ReadOnlySpan<byte> parameters = default);
}
