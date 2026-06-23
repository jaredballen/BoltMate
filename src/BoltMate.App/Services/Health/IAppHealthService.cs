using System;

namespace BoltMate.App.Services;

/// <summary>
/// App-wide health monitor. Aggregates OS-permission, network-transport,
/// and receiver-attached state into a single <see cref="AppHealthSnapshot"/>
/// stream that drives the tray badge + one-shot OS notifications on
/// transitions into a bad state.
/// </summary>
public interface IAppHealthService : IDisposable
{
    /// <summary>Live health snapshot — current + every subsequent change.</summary>
    IObservable<AppHealthSnapshot> Health { get; }

    /// <summary>Latest snapshot without subscribing.</summary>
    AppHealthSnapshot Current { get; }
}
