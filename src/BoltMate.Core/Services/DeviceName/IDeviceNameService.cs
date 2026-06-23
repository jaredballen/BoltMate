namespace BoltMate.Core.Services;

/// <summary>
/// HID++ 2.0 DEVICE_NAME (feature 0x0005). Read the product name in chunks.
/// </summary>
public interface IDeviceNameService
{
    Task<int> GetNameCountAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default);
    Task<string> GetNameAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default);
}
