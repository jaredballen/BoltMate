using System;
using System.Threading;
using System.Threading.Tasks;

namespace BoltMate.LicenseApi.Services;

/// <summary>
/// Transactional email outbox for the LicenseApi. Implemented by
/// <see cref="ResendEmailNotifier"/> in production; a no-op fake is
/// used in tests + the local-dev "no Resend key" path.
/// </summary>
/// <remarks>
/// Each method maps to one Resend template. Bodies are inline string
/// builders rather than separate HTML files so the entire mailer ships
/// in one Function deploy without a template-loading runtime.
/// </remarks>
public interface IEmailNotifier
{
    /// <summary>Sent on Stripe checkout.session.completed once the
    /// license has been upserted at Boltmate tier.</summary>
    Task PurchaseConfirmationAsync(string toEmail, string licenseId, CancellationToken ct = default);

    /// <summary>Sent T-3 days and T-1 days before a Trial expires.
    /// <paramref name="daysLeft"/> drives the copy.</summary>
    Task TrialEndingAsync(string toEmail, int daysLeft, DateTimeOffset expiresAt, CancellationToken ct = default);

    /// <summary>Sent on the day a Trial license actually crosses its
    /// ExpiresAt — last conversion nudge.</summary>
    Task TrialExpiredAsync(string toEmail, CancellationToken ct = default);

    /// <summary>Auto-ack to the support submitter so they know the
    /// ticket landed. Separate from the human-readable email that goes
    /// to <c>support@boltmate.app</c>.</summary>
    Task SupportTicketAckAsync(string toEmail, string subject, CancellationToken ct = default);
}
