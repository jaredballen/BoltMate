using System;
using BoltMate.Licensing.Contracts;

namespace BoltMate.LicenseApi.Models;

/// <summary>
/// Persisted license row in Cosmos `Licenses` container.
/// Partition key is normalized email so per-user lookups are point-reads.
/// </summary>
public sealed class LicenseRecord
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Cosmos partition key — same value as <see cref="Email"/> normalized
    /// (lowercase, trimmed). Required field; do not leave blank.</summary>
    public string PartitionKey { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    /// <summary>OAuth `sub` claim from the originating ID token (B2C `oid`,
    /// Google `sub`, etc.). Stable identifier across email changes if the
    /// provider supports it.</summary>
    public string? OAuthSubject { get; set; }

    public string Sku { get; set; } = string.Empty;

    /// <summary>Tier the license currently grants. Every caller MUST set
    /// this explicitly when creating a record (Trial on auto-provision,
    /// Boltmate on Stripe upgrade). Default Trial is just a safe fallback
    /// — there is no "Free" or "None" tier; unentitled state is signalled
    /// by deleting the record (or by absence on the desktop side).</summary>
    public LicenseTier Tier { get; set; } = LicenseTier.Trial;

    public string Status { get; set; } = "active";

    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>Hard expiration of the tier grant. Null for paid lifetime
    /// licenses; set to IssuedAt+14d for Trial.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>SHA-256 of the desktop hardware ID at the time a Trial
    /// was first granted. Used to block re-trial farming within 12 mo
    /// even if the user signs up with a different OAuth account.</summary>
    public string? HardwareIdHash { get; set; }

    /// <summary>When a Trial was first granted to this email. Survives
    /// upgrades — even after Boltmate is purchased we keep this so the
    /// 12-mo re-trial block stays enforced if the user later refunds
    /// and tries again.</summary>
    public DateTimeOffset? TrialOriginAt { get; set; }

    public string? StripeCustomerId { get; set; }
    public string? StripeCheckoutSessionId { get; set; }
    public string? StripePaymentIntentId { get; set; }

    /// <summary>
    /// Base64-encoded AES-256 key shared by every machine signed in
    /// under this account. Drives the AES-GCM envelope that wraps
    /// every cross-machine UDP + TCP frame. Generated at Trial
    /// provisioning, preserved across Stripe upgrade + refund flows
    /// so a user's existing peer trust ring isn't broken by paying.
    /// Rotating this value invalidates every cached peer key on the
    /// next entitlement refresh — destructive operation, do not
    /// regenerate casually.
    /// </summary>
    public string? SyncKeyBase64 { get; set; }

    /// <summary>Set once the T-3 trial reminder email has been sent so
    /// the daily TimerTrigger never sends it twice. Cleared only when
    /// the row is hard-deleted.</summary>
    public bool TrialNotifiedT3 { get; set; }

    /// <summary>T-1 reminder send dedup flag — see <see cref="TrialNotifiedT3"/>.</summary>
    public bool TrialNotifiedT1 { get; set; }

    /// <summary>"Trial just expired" send dedup flag.</summary>
    public bool TrialNotifiedExpired { get; set; }
}
