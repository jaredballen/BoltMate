using System.Reactive.Subjects;
using BoltMate.Core.Services;

namespace BoltMate.Tests.Support;

/// <summary>Test double for INetworkAvailabilityWatcher.</summary>
public sealed class FakeNetworkAvailabilityWatcher : INetworkAvailabilityWatcher
{
    private readonly BehaviorSubject<bool> _subject;

    public FakeNetworkAvailabilityWatcher(bool initial = true)
    {
        _subject = new BehaviorSubject<bool>(initial);
    }

    public bool IsAvailable => _subject.Value;
    public IObservable<bool> IsAvailableChanged => _subject;
    public void Set(bool available) => _subject.OnNext(available);
    public void Dispose() { try { _subject.Dispose(); } catch { } }
}
