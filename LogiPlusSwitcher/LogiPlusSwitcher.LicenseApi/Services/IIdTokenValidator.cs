using System.Threading;
using System.Threading.Tasks;

namespace LogiPlusSwitcher.LicenseApi.Services;

public interface IIdTokenValidator
{
    Task<ValidatedIdToken?> ValidateAsync(string idToken, CancellationToken ct = default);
}

public sealed record ValidatedIdToken(string Subject, string Email, string? Name);
