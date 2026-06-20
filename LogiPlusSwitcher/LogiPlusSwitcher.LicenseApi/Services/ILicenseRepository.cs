using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LogiPlusSwitcher.LicenseApi.Models;

namespace LogiPlusSwitcher.LicenseApi.Services;

public interface ILicenseRepository
{
    Task<LicenseRecord?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<IReadOnlyList<LicenseRecord>> ListByEmailAsync(string email, CancellationToken ct = default);
    Task UpsertAsync(LicenseRecord record, CancellationToken ct = default);
    Task BindOAuthSubjectAsync(string licenseId, string email, string oauthSubject, CancellationToken ct = default);
}
