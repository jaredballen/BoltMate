using System;
using System.Threading;
using System.Threading.Tasks;

namespace BoltMate.LicenseApi.Services;

public interface IRefreshLogRepository
{
    Task RecordAsync(string licenseId, string oauthSubject, DateTimeOffset at, CancellationToken ct = default);
    Task<int> CountSinceAsync(string licenseId, DateTimeOffset since, CancellationToken ct = default);
}
