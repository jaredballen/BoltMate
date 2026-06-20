using System.Threading;
using System.Threading.Tasks;
using LogiPlusSwitcher.Licensing.Contracts;

namespace LogiPlusSwitcher.Licensing.Activation;

public interface IEntitlementClient
{
    Task<EntitlementResponse> RequestEntitlementAsync(string idToken, CancellationToken ct = default);
}

public sealed class EntitlementRequestException : System.Exception
{
    public EntitlementRequestException(string code, string? message, int? retryAfterSeconds)
        : base(message ?? code)
    {
        Code = code;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public string Code { get; }
    public int? RetryAfterSeconds { get; }

    public bool IsRevoked => Code == "license_revoked";
    public bool IsNotFound => Code == "license_not_found";
    public bool IsRateLimited => Code == "rate_limited";
}
