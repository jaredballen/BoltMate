using System;
using BoltMate.Licensing.Contracts;

namespace BoltMate.Licensing;

public sealed record LicenseStatus(
    LicenseState State,
    LicenseTier Tier,
    string? Email,
    string? LicenseId,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RefreshFailedSince)
{
    public bool IsEntitled => State is LicenseState.Valid or LicenseState.GracePeriod;

    public static LicenseStatus NotActivated { get; } = new(
        LicenseState.NotActivated, LicenseTier.Free, null, null, null, null, null);
}
