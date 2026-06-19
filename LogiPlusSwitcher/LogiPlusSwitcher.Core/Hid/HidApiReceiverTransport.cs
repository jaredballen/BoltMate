using HidApi;
using LogiPlusSwitcher.Core.Bolt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogiPlusSwitcher.Core.Hid;

/// <summary>
/// libhidapi-backed transport. Wires up the macOS non-exclusive flag at
/// construction time so subsequent opens coexist with Logi Options+.
/// </summary>
public sealed class HidApiReceiverTransport : IReceiverTransport
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HidApiReceiverTransport> _logger;

    public HidApiReceiverTransport(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<HidApiReceiverTransport>();
        HidApiBridge.EnsureNativeLibraryResolver();
        // Best-effort on macOS; no-op (returns false) on Windows/Linux.
        var nonExclusive = HidApiBridge.SetMacOsNonExclusive();
        _logger.LogInformation("HID transport initialised; macOS non-exclusive open = {NonExclusive}", nonExclusive);
    }

    public IReadOnlyList<BoltReceiverInfo> Enumerate()
    {
        var infos = HidApi.Hid.Enumerate(BoltConstants.LogitechVendorId, BoltConstants.BoltReceiverProductId);
        var result = BoltReceiverInfo.Filter(infos).ToList();
        _logger.LogDebug("Enumerated {Count} Bolt receiver management interface(s)", result.Count);
        return result;
    }

    public IReceiverConnection Open(BoltReceiverInfo info)
    {
        _logger.LogInformation("Opening receiver {Product} at {Path}", info.ProductString, info.Path);
        var device = new Device(info.Path);
        return new HidApiReceiverConnection(device, _loggerFactory.CreateLogger<HidApiReceiverConnection>());
    }
}
