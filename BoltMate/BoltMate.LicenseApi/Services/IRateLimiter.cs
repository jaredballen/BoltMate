using System.Threading;
using System.Threading.Tasks;

namespace BoltMate.LicenseApi.Services;

public interface IRateLimiter
{
    Task<RateLimitDecision> CheckAsync(string licenseId, CancellationToken ct = default);
}

public sealed record RateLimitDecision(bool Allowed, int? RetryAfterSeconds, string? Reason);
