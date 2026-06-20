using System.Threading;
using System.Threading.Tasks;
using LogiPlusSwitcher.Licensing.Contracts;

namespace LogiPlusSwitcher.LicenseApi.Services;

public interface IJwtSigner
{
    Task<string> SignAsync(LicenseClaims claims, CancellationToken ct = default);
}
