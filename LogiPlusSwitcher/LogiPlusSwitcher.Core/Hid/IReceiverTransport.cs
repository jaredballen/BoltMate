namespace LogiPlusSwitcher.Core.Hid;

/// <summary>
/// Discovers Bolt receivers and opens a HID++ management-interface connection
/// to one of them. Abstracted so we can swap the underlying HID library
/// (libhidapi today, WinRT HID for the eventual Microsoft Store build, etc.).
/// </summary>
public interface IReceiverTransport
{
    /// <summary>
    /// Returns every Bolt receiver currently attached (the HID++ management
    /// interface of each).
    /// </summary>
    IReadOnlyList<BoltReceiverInfo> Enumerate();

    /// <summary>
    /// Opens a connection to the management interface of <paramref name="info"/>.
    /// Must NOT take exclusive ownership of the device — Logi Options+ keeps it
    /// open simultaneously and we are a coexisting observer.
    /// </summary>
    IReceiverConnection Open(BoltReceiverInfo info);
}
