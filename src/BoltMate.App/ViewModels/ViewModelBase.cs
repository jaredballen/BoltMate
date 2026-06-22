using System.Reactive.Disposables;
using ReactiveUI;

namespace BoltMate.App.ViewModels;

/// <summary>
/// Common base for all view-models in the app. Inherits ReactiveUI's
/// <see cref="ReactiveObject"/> for INotifyPropertyChanged + Rx-flavored
/// property bindings, and exposes a <see cref="CompositeDisposable"/>
/// for window/view-bound subscription cleanup.
/// </summary>
/// <remarks>
/// Avalonia.ReactiveUI integration package hasn't shipped a 12.x release
/// (tops out at 11.3.9 against Avalonia 11.x), so we don't yet have
/// <c>ReactiveWindow&lt;T&gt; + WhenActivated</c>. Window code-behind
/// activates / deactivates its view-model via the
/// <see cref="Activate"/> / <see cref="Deactivate"/> hooks below,
/// typically wired to <c>Window.Opened</c> / <c>Window.Closed</c>.
/// Migrate to the official Avalonia.ReactiveUI helpers once they're
/// released for Avalonia 12.x.
/// </remarks>
public abstract class ViewModelBase : ReactiveObject
{
    /// <summary>
    /// Disposables that should live for the lifetime of the view-model.
    /// Subscriptions added here are cleaned up by <see cref="Deactivate"/>.
    /// </summary>
    protected CompositeDisposable Disposables { get; } = new();

    /// <summary>
    /// Hook for view-bound activation. Override to wire up subscriptions
    /// when the view this VM backs becomes visible. Base implementation is
    /// a no-op.
    /// </summary>
    public virtual void Activate() { }

    /// <summary>
    /// Hook for view-bound deactivation. Disposes <see cref="Disposables"/>
    /// by default — override to extend, not to replace. Idempotent.
    /// </summary>
    public virtual void Deactivate()
    {
        Disposables.Dispose();
    }
}
