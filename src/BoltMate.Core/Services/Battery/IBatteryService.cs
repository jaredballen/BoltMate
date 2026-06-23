namespace BoltMate.Core.Services;

/// <summary>
/// Read the battery snapshot for a slot via HID++ 2.0 feature 0x1004
/// (UNIFIED_BATTERY).
/// </summary>
public interface IBatteryService
{
    /// <summary>
    /// Reads percent / charging state / external power presence / discrete
    /// level. Returns null if the device doesn't expose UNIFIED_BATTERY or
    /// the read fails.
    /// </summary>
    Task<BatteryStatus?> GetStatusAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default);
}
