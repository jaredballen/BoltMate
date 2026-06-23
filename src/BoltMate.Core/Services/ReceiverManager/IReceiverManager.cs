using DynamicData;

namespace BoltMate.Core.Services;

/// <summary>
/// Lifecycle owner for the set of currently-attached Bolt receivers.
/// Polls the underlying transport, opens a <see cref="BoltReceiver"/>
/// per attached device, and disposes it when the device disappears.
/// </summary>
public interface IReceiverManager : IDisposable
{
    /// <summary>How often the manager re-enumerates the transport.</summary>
    TimeSpan PollInterval { get; }

    /// <summary>Live cache of currently-attached Bolt receivers, keyed by HID path.</summary>
    IObservableCache<BoltReceiver, string> Receivers { get; }

    /// <summary>
    /// Stream of attach failures — open threw because the device went
    /// away mid-open or the OS rejected the open. Surfaces to the UI so
    /// the user can be told why a receiver did not light up.
    /// </summary>
    IObservable<Exception> AttachFailures { get; }

    /// <summary>
    /// Forces an immediate reconciliation. Useful for tests and for the
    /// initial pass when polling is disabled.
    /// </summary>
    void Refresh();
}
