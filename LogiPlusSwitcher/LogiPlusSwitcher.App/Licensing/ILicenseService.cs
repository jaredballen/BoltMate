using System;

namespace LogiPlusSwitcher.App.Licensing;

/// <summary>
/// Drives the Free/Pro tier UI. Wraps whatever license/entitlement source
/// the app uses (local key, Microsoft Store entitlement, App Store receipt).
/// Core stays unaware of this — gating happens at the App layer only.
/// </summary>
public interface ILicenseService
{
    /// <summary>True if Pro features (write-to-receiver, pair, etc.) are unlocked.</summary>
    bool IsPro { get; }

    /// <summary>Live stream of <see cref="IsPro"/> changes (e.g. user enters a license key, trial expires).</summary>
    IObservable<bool> IsProChanges { get; }
}
