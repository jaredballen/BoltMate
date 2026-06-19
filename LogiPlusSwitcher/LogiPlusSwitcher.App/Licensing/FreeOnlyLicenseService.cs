using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace LogiPlusSwitcher.App.Licensing;

/// <summary>
/// License service that reports Free unconditionally. Useful for testing the
/// upsell flow without a real licensing backend.
/// </summary>
public sealed class FreeOnlyLicenseService : ILicenseService
{
    private readonly BehaviorSubject<bool> _proSubject = new(false);

    public bool IsPro => _proSubject.Value;
    public IObservable<bool> IsProChanges => _proSubject.AsObservable();
}
