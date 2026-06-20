using System;
using System.Threading;
using System.Threading.Tasks;

namespace LogiPlusSwitcher.Licensing;

public interface ILicenseGate
{
    LicenseStatus Current { get; }

    IObservable<LicenseStatus> StatusChanges { get; }

    Task<LicenseStatus> LoadAsync(CancellationToken ct = default);

    Task<LicenseStatus> ActivateAsync(CancellationToken ct = default);

    Task<LicenseStatus> RefreshAsync(CancellationToken ct = default);

    Task SignOutAsync(CancellationToken ct = default);
}
