namespace BoltMate.Core.Services;

/// <summary>
/// HID++ 2.0 DEVICE_FRIENDLY_NAME (feature 0x0007). Read / write the
/// user-editable device nickname.
/// </summary>
public interface IDeviceFriendlyNameService
{
    Task<int> GetLengthAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default);
    Task<string> GetAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default);
    Task SetFriendlyNameAsync(byte deviceIndex, byte featureIndex, string name, CancellationToken ct = default);
    Task ResetFriendlyNameAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default);
}
