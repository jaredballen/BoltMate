using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace LogiPlusSwitcher.App.Licensing;

/// <summary>
/// Development stub that unconditionally reports Pro. Replace with a real
/// license/entitlement service before shipping a paid build. The Avalonia
/// composition root picks this up via DI / direct instantiation, so swapping
/// it later is a one-line change.
/// </summary>
public sealed class DevAlwaysProLicenseService : ILicenseService
{
    private readonly BehaviorSubject<bool> _proSubject = new(true);

    public bool IsPro => _proSubject.Value;
    public IObservable<bool> IsProChanges => _proSubject.AsObservable();
}
