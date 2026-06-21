using System.Threading;
using System.Threading.Tasks;
using BoltMate.Licensing.Contracts;

namespace BoltMate.LicenseApi.Services;

public interface IJwtSigner
{
    Task<string> SignAsync(LicenseClaims claims, CancellationToken ct = default);
}
