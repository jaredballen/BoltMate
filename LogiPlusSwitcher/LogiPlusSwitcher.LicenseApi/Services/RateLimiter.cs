using System;
using System.Threading;
using System.Threading.Tasks;
using LogiPlusSwitcher.LicenseApi.Configuration;
using Microsoft.Extensions.Options;

namespace LogiPlusSwitcher.LicenseApi.Services;

internal sealed class RateLimiter : IRateLimiter
{
    private readonly IRefreshLogRepository _log;
    private readonly LicenseApiOptions _options;

    public RateLimiter(IRefreshLogRepository log, IOptions<LicenseApiOptions> options)
    {
        _log = log;
        _options = options.Value;
    }

    public async Task<RateLimitDecision> CheckAsync(string licenseId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var day = now - TimeSpan.FromHours(24);
        var month = now - TimeSpan.FromDays(30);

        var dayCount = await _log.CountSinceAsync(licenseId, day, ct).ConfigureAwait(false);
        if (dayCount >= _options.Refresh24hCap)
            return new RateLimitDecision(false, (int)TimeSpan.FromHours(24).TotalSeconds, "24h cap exceeded");

        var monthCount = await _log.CountSinceAsync(licenseId, month, ct).ConfigureAwait(false);
        if (monthCount >= _options.Refresh30dCap)
            return new RateLimitDecision(false, (int)TimeSpan.FromDays(7).TotalSeconds, "30d cap exceeded");

        return new RateLimitDecision(true, null, null);
    }
}
