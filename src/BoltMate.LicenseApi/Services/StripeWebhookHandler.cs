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
                _log.LogInformation("Ignoring Stripe event {Type}", stripeEvent.Type);
                break;
        }
    }

    private async Task HandleCheckoutCompletedAsync(Session session, CancellationToken ct)
    {
        var email = session.CustomerDetails?.Email ?? session.CustomerEmail;
        if (string.IsNullOrWhiteSpace(email))
        {
            _log.LogWarning("Checkout session {Id} missing customer email.", session.Id);
            return;
        }

        var sku = session.Metadata is { } meta && meta.TryGetValue("sku", out var s) ? s : LicenseSkus.Pro;

        var record = new LicenseRecord
        {
            Id = $"lic_{Guid.NewGuid():N}",
            Email = email,
            Sku = sku,
            Tier = LicenseTier.Pro,
            Status = "active",
            IssuedAt = DateTimeOffset.UtcNow,
            StripeCustomerId = session.CustomerId,
            StripeCheckoutSessionId = session.Id,
            StripePaymentIntentId = session.PaymentIntentId
        };

        await _licenses.UpsertAsync(record, ct).ConfigureAwait(false);
        _log.LogInformation("Issued license {LicenseId} to {Email}.", record.Id, email);
    }

    private Task HandleRefundOrDisputeAsync(Charge charge, CancellationToken ct)
    {
        _log.LogWarning("TODO: revoke license for charge {Charge} customer {Customer}", charge.Id, charge.CustomerId);
        return Task.CompletedTask;
    }
}
