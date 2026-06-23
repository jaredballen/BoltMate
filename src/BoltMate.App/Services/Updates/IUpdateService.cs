using System;
using System.Threading;
using System.Threading.Tasks;

namespace BoltMate.App.Services;

/// <summary>
/// Scaffolds the update-check loop. v1 has no backing cast server —
/// <see cref="CheckAsync"/> always reports "up to date". Lives behind
/// the interface so the real Sparkle / JSON-manifest implementation
/// drops in without UI churn later.
/// </summary>
public interface IUpdateService
{
    /// <summary>Version string baked into the assembly by Nerdbank.GitVersioning.</summary>
    string CurrentVersion { get; }

    /// <summary>When the last check ran, or null if never.</summary>
    DateTimeOffset? LastCheckUtc { get; }

    /// <summary>
    /// Performs an update check. Returns the available update info or
    /// null when already on the latest. Stamps <see cref="LastCheckUtc"/>
    /// even when no update is found.
    /// </summary>
    Task<UpdateInfo?> CheckAsync(CancellationToken ct = default);
}
