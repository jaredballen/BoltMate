using System.Reactive.Subjects;
using BoltMate.Core.Permissions;

namespace BoltMate.Tests.Support;

/// <summary>
/// Minimal IPermission for tests. Lets the test drive grant/revoke
/// transitions deterministically without standing up the real
/// PermissionsService poller.
/// </summary>
public sealed class FakePermission : IPermission
{
    private readonly BehaviorSubject<bool> _subject;

    public FakePermission(string name, bool initial = true)
    {
        Name = name;
        _subject = new BehaviorSubject<bool>(initial);
    }

    public string Name { get; }
    public bool IsGranted => _subject.Value;
    public IObservable<bool> IsGrantedChanged => _subject;
    public bool CanRevoke => true;

    public Task<bool> GrantAsync(CancellationToken ct = default)
    {
        _subject.OnNext(true);
        return Task.FromResult(true);
    }

    public Task<bool> RevokeAsync(CancellationToken ct = default)
    {
        _subject.OnNext(false);
        return Task.FromResult(true);
    }

    public void AcknowledgeExternalGrant() => _subject.OnNext(true);

    public void Set(bool granted) => _subject.OnNext(granted);
}
