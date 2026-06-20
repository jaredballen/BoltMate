namespace BoltMate.Hid.Abstractions;

/// <summary>
/// Identifies a single Logitech Bolt receiver's HID++ management interface
/// (the only one of its sibling HID interfaces that carries HID++ traffic).
/// </summary>
/// <remarks>
/// Each platform transport produces these (IOKit on macOS, libhidapi on
/// Win/Linux). The <see cref="Path"/> string format is opaque to consumers
/// and only meaningful to the transport that produced it.
/// </remarks>
public sealed record BoltReceiverInfo(
    string Path,
    string Serial,
    string ProductString,
    string ManufacturerString,
    ushort ReleaseNumber);
