using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using LogiPlusSwitcher.App.Licensing;

namespace LogiPlusSwitcher.Tests.Support;

/// <summary>
/// Test double for <see cref="ILicenseService"/>. Lets a test flip Pro state
/// at will to exercise <see cref="ReceiverPolicyService"/> transitions.
/// </summary>
public sealed class StubLicenseService : ILicenseService
{
    private readonly BehaviorSubject<bool> _subject;

    public StubLicenseService(bool initialPro = false)
    {
        _subject = new BehaviorSubject<bool>(initialPro);
    }

    public bool IsPro => _subject.Value;
    public IObservable<bool> IsProChanges => _subject.AsObservable();

    public void SetPro(bool value) => _subject.OnNext(value);
}
