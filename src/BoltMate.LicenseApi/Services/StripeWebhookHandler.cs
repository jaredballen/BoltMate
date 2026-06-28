using System;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.LicenseApi.Configuration;
using BoltMate.LicenseApi.Models;
using BoltMate.Licensing.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace BoltMate.LicenseApi.Services;

/// <summary>
/// Routes Stripe webhook events into license-state mutations.
/// </summary>
/// <remarks>
/// <para>Idempotency: Stripe retries on any non-2xx, plus they can
/// occasionally deliver the same event twice. We don't keep a separate
/// events-processed log — instead each handler checks the current
/// license shape and short-circuits if the mutation has already been
/// applied (e.g. Boltmate tier + matching CheckoutSessionId means the
/// upgrade landed; status="revoked" means the refund landed).</para>
///
/// <para>Trial → Boltmate upgrade preserves the existing record's
/// HardwareIdHash + TrialOriginAt so the 12-mo trial-reuse block stays
/// enforceable if the user later refunds and tries to start over.</para>
/// </remarks>
internal sealed class StripeWebhookHandler : IStripeWebhookHandler
{
    private readonly LicenseApiOptions _options;
    private readonly ILicenseRepository _licenses;
    private readonly ILogger<StripeWebhookHandler> _log;

    public StripeWebhookHandler(IOptions<LicenseApiOptions> options, ILicenseRepository licenses, ILogger<StripeWebhookHandler> log)
    {
        _options = options.Value;
        _licenses = licenses;
        _log = log;
    }

    public async Task HandleAsync(string payload, string signature, CancellationToken ct = default)
    {
        var stripeEvent = EventUtility.ConstructEvent(payload, signature, _options.StripeWebhookSecret);
        await DispatchAsync(stripeEvent, ct).ConfigureAwait(false);
    }

    // Internal seam — lets tests skip signature verification by handing
    // in an already-parsed Event. Production caller always goes through
    // HandleAsync which performs the signature check first.
    internal async Task DispatchAsync(Event stripeEvent, CancellationToken ct = default)
    {
        switch (stripeEvent.Type)
        {
            case "checkout.session.completed":
                if (stripeEvent.Data.Object is Session session)
                    await HandleCheckoutCompletedAsync(session, ct).ConfigureAwait(false);
                break;

            case "charge.refunded":
            case "charge.dispute.created":
                if (stripeEvent.Data.Object is Charge charge)
                    await HandleRefundOrDisputeAsync(charge, ct).ConfigureAwait(false);
                break;

            default:
                _log.LogInformation("Ignoring Stripe event {Type}.", stripeEvent.Type);
                break;
        }
    }

    private async Task HandleCheckoutCompletedAsync(Session session, CancellationToken ct)
    {
        var email = session.CustomerDetails?.Email ?? session.CustomerEmail;
        if (string.IsNullOrWhiteSpace(email))
        {
            _log.LogWarning("Checkout session {Id} missing customer email — cannot bind license.", session.Id);
            return;
        }

        var sku = session.Metadata is { } meta && meta.TryGetValue("sku", out var s) ? s : LicenseSkus.Boltmate;
        var now = DateTimeOffset.UtcNow;

        var existing = await _licenses.GetByEmailAsync(email, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            // Idempotency: if we already processed this exact session,
            // bail. Lets Stripe retry safely.
            if (existing.Tier == LicenseTier.Boltmate
                && existing.StripeCheckoutSessionId == session.Id)
            {
                _log.LogInformation("Checkout {SessionId} already applied to {LicenseId}; skipping.",
                    session.Id, existing.Id);
                return;
            }

            // Upgrade in place — preserves HardwareIdHash + TrialOriginAt
            // so the 12-mo trial-reuse block survives any future refund.
            existing.Sku = sku;
            existing.Tier = LicenseTier.Boltmate;
            existing.Status = "active";
            existing.ExpiresAt = null;   // lifetime license — no clamp
            existing.RevokedAt = null;
            existing.StripeCustomerId = session.CustomerId;
            existing.StripeCheckoutSessionId = session.Id;
            existing.StripePaymentIntentId = session.PaymentIntentId;
            await _licenses.UpsertAsync(existing, ct).ConfigureAwait(false);
            _log.LogInformation("Upgraded {LicenseId} → Boltmate for {Email}.", existing.Id, email);
            return;
        }

        // No existing license — fresh purchase before any Trial provision
        // (rare: user purchased directly from the site without ever launching
        // the desktop app). Create the record straight at Boltmate tier.
        var record = new LicenseRecord
        {
            Id = $"lic_{Guid.NewGuid():N}",
            Email = email,
            Sku = sku,
            Tier = LicenseTier.Boltmate,
            Status = "active",
            IssuedAt = now,
            ExpiresAt = null,
            StripeCustomerId = session.CustomerId,
            StripeCheckoutSessionId = session.Id,
            StripePaymentIntentId = session.PaymentIntentId,
        };
        await _licenses.UpsertAsync(record, ct).ConfigureAwait(false);
        _log.LogInformation("Issued {LicenseId} (Boltmate) to {Email} from session {SessionId}.",
            record.Id, email, session.Id);
    }

    private async Task HandleRefundOrDisputeAsync(Charge charge, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(charge.PaymentIntentId))
        {
            _log.LogWarning("Refund event for charge {ChargeId} carried no PaymentIntent; cannot map to license.",
                charge.Id);
            return;
        }

        var license = await _licenses
            .GetByStripePaymentIntentIdAsync(charge.PaymentIntentId, ct)
            .ConfigureAwait(false);
        if (license is null)
        {
            _log.LogWarning("Refund event for PaymentIntent {Pi} has no matching license.", charge.PaymentIntentId);
            return;
        }

        if (license.Status == "revoked")
        {
            _log.LogInformation("License {LicenseId} already revoked; skipping refund event.", license.Id);
            return;
        }

        license.Status = "revoked";
        license.RevokedAt = DateTimeOffset.UtcNow;
        // Drop tier back to Trial so the entitlement JWT's tier claim
        // can't grant Boltmate features post-refund. Status check on
        // the desktop side is the primary gate, but defense in depth.
        license.Tier = LicenseTier.Trial;
        license.ExpiresAt = license.RevokedAt; // hard-expire immediately
        await _licenses.UpsertAsync(license, ct).ConfigureAwait(false);

        _log.LogWarning("Revoked {LicenseId} ({Email}) due to charge {ChargeId}.",
            license.Id, license.Email, charge.Id);
    }
}
