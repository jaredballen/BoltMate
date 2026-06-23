using ReactiveUI;

namespace BoltMate.App.UI;

/// <summary>
/// Common base for all view-models in the app. Inherits ReactiveUI's
/// <see cref="ReactiveObject"/> for INotifyPropertyChanged + Rx-flavored
/// property bindings.
/// </summary>
/// <remarks>
/// Windows backed by a view-model should inherit
/// <c>ReactiveWindow&lt;TViewModel&gt;</c> from <c>Avalonia.ReactiveUI</c>
/// and put activation/deactivation subscriptions inside a
/// <c>this.WhenActivated(d =&gt; { ... d.Add(...) })</c> block in the
/// view's constructor — that's the canonical lifecycle pattern and
/// works against Avalonia 12 even though the integration package
/// version pinned in the csproj is 11.3.9.
/// </remarks>
public abstract class ViewModelBase : ReactiveObject
{
}
