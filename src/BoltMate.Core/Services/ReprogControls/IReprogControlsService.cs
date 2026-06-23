namespace BoltMate.Core.Services;

/// <summary>
/// HID++ 2.0 REPROG_CONTROLS_V4 (feature 0x1B04). Enumerate + reconfigure
/// reprogrammable controls (Easy-Switch buttons, gesture buttons, etc.).
/// </summary>
public interface IReprogControlsService
{
    Task<int> GetControlCountAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default);
    Task<ControlInfo> GetCidInfoAsync(byte deviceIndex, byte featureIndex, byte controlIndex, CancellationToken ct = default);
    Task<IReadOnlyList<ControlInfo>> ListControlsAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default);
    Task<CidReportingState> GetCidReportingAsync(byte deviceIndex, byte featureIndex, ushort controlId, CancellationToken ct = default);
    Task SetCidReportingAsync(byte deviceIndex, byte featureIndex, ushort controlId, byte bfield, CancellationToken ct = default);
}
