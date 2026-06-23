namespace BoltMate.Core.Services;

/// <summary>
/// HID++ 2.0 DEVICE_INFO (feature 0x0003). Firmware version + serial number reads.
/// </summary>
public interface IDeviceInfoService
{
    Task<DeviceFirmwareInfo?> GetFirmwareAsync(byte deviceIndex, byte featureIndex, byte entityIndex = 0, CancellationToken ct = default);
    Task<string?> GetSerialAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default);
}
