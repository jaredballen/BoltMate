using System;

namespace LogiPlusSwitcher.Licensing.Contracts;

public sealed record LicenseClaims(
    string Subject,
    string Email,
    string LicenseId,
    string Sku,
    LicenseTier Tier,
    string Issuer,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);
