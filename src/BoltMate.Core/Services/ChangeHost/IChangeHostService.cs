namespace BoltMate.Core.Services;

/// <summary>
/// HID++ 2.0 CHANGE_HOST (feature 0x1814). Read current host + write to
/// switch hosts (fire-and-forget).
/// </summary>
public interface IChangeHostService
{
    Task<HostInfo> GetHostInfoAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default);
    void SetCurrentHost(byte deviceIndex, byte featureIndex, byte targetHost);
}
