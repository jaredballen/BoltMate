namespace BoltMate.Core.Services;

/// <summary>
/// HID++ 2.0 HOSTS_INFO (feature 0x1815). Discover paired hosts + read
/// their friendly names. Read-only; no host-switch events are pushed.
/// </summary>
public interface IHostsInfoService
{
    Task<HostsInfo> GetHostsInfoAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default);
    Task<Bolt.HostBinding> GetHostInfoAsync(byte deviceIndex, byte featureIndex, byte hostIndex, CancellationToken ct = default);
    Task<IReadOnlyList<Bolt.HostBinding>> GetAllHostsAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default);
    Task<string> GetHostFriendlyNameAsync(byte deviceIndex, byte featureIndex, byte hostIndex, CancellationToken ct = default);
}
