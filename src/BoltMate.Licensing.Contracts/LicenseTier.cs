namespace BoltMate.Licensing.Contracts;

/// <summary>
/// Tier the license is bound to. Stored on every license record and
/// stamped into the signed entitlement JWT so the desktop app can gate
/// behavior offline.
/// </summary>
/// <remarks>
/// Only two tiers exist: <see cref="Trial"/> (14-day auto-provisioned)
/// and <see cref="Boltmate"/> (paid lifetime). Unentitled states
/// (NotActivated / Revoked / Expired / SignatureInvalid) are conveyed
/// via <c>LicenseStatus.State</c> with a nullable <c>Tier</c>; they do
/// NOT get an enum value here.
/// Numeric ordering preserved so callers can use comparison for
/// "at least tier X" semantics. Don't reorder; only append.
/// </remarks>
public enum LicenseTier
{
    /// <summary>14-day auto-provisioned trial granted on first successful sign-in.</summary>
    Trial = 1,

    /// <summary>Paid lifetime license — granted on successful Stripe checkout.</summary>
    Boltmate = 2,
}
