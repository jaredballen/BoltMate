using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.LicenseApi.Models;

namespace BoltMate.LicenseApi.Services;

public interface ILicenseRepository
{
    Task<LicenseRecord?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<IReadOnlyList<LicenseRecord>> ListByEmailAsync(string email, CancellationToken ct = default);
    Task UpsertAsync(LicenseRecord record, CancellationToken ct = default);
    Task BindOAuthSubjectAsync(string licenseId, string email, string oauthSubject, CancellationToken ct = default);

    /// <summary>
    /// Returns true if any license record exists with the given hardware
    /// ID hash whose <c>TrialOriginAt</c> is on or after the supplied
    /// cutoff. Used by EntitlementFunction to block re-trial farming
    /// (same hardware, different email).
    /// </summary>
    Task<bool> HasRecentTrialForHardwareAsync(string hardwareIdHash, DateTimeOffset cutoffUtc, CancellationToken ct = default);

    /// <summary>
    /// Returns the license bound to a Stripe payment intent, or null if
    /// none. Used by the refund webhook handler — Stripe sends us the
    /// PaymentIntent ID on <c>charge.refunded</c>; we reverse-lookup the
    /// license that was issued for it. Cross-partition query (any email).
    /// </summary>
    Task<LicenseRecord?> GetByStripePaymentIntentIdAsync(string paymentIntentId, CancellationToken ct = default);

    /// <summary>
    /// Hard-deletes every license record under the given email partition.
    /// Used by the GDPR account-deletion endpoint. Idempotent — returns
    /// the number of rows actually removed (0 when the email had nothing).
    /// </summary>
    Task<int> DeleteByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Cross-partition list of every active Trial whose <c>ExpiresAt</c>
    /// falls within the half-open window <c>[from, to)</c>. Drives the
    /// daily trial-reminder TimerTrigger. Only includes rows whose
    /// matching <c>TrialNotifiedXxx</c> flag is still false so the
    /// caller can short-circuit dedup.
    /// </summary>
    /// <param name="from">Inclusive lower bound of ExpiresAt.</param>
    /// <param name="to">Exclusive upper bound of ExpiresAt.</param>
    /// <param name="notifiedFlag">Discriminates which flag to check: "t3", "t1", or "expired".</param>
    Task<IReadOnlyList<LicenseRecord>> ListActiveTrialsExpiringBetweenAsync(
        DateTimeOffset from, DateTimeOffset to, string notifiedFlag, CancellationToken ct = default);
}
