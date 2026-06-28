using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BoltMate.LicenseApi.Configuration;
using BoltMate.LicenseApi.Models;
using BoltMate.LicenseApi.Services;
using BoltMate.Licensing.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace BoltMate.Licensing.Tests;

public sealed class StripeWebhookHandlerTests
{
    [Fact]
    public async Task Checkout_completed_upgrades_trial_in_place()
    {
        var (handler, fakes) = Build();
        fakes.Licenses.ByEmail["jared@example.com"] = new LicenseRecord
        {
            Id = "lic_existing",
            Email = "jared@example.com",
            Sku = LicenseSkus.Boltmate,
            Tier = LicenseTier.Trial,
            Status = "active",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(5),
            HardwareIdHash = "hw-1",
            TrialOriginAt = DateTimeOffset.UtcNow.AddDays(-9),
        };

        await handler.DispatchAsync(CheckoutEvent("sess_1", "pi_1", "jared@example.com"));

        var rec = fakes.Licenses.ByEmail["jared@example.com"];
        Assert.Equal(LicenseTier.Boltmate, rec.Tier);
        Assert.Null(rec.ExpiresAt);
        Assert.Equal("sess_1", rec.StripeCheckoutSessionId);
        // Hardware + trial origin preserved so the 12-mo block survives refund.
        Assert.Equal("hw-1", rec.HardwareIdHash);
        Assert.NotNull(rec.TrialOriginAt);
        Assert.Equal("lic_existing", rec.Id);
    }

    [Fact]
    public async Task Checkout_completed_redelivery_is_idempotent()
    {
        var (handler, fakes) = Build();
        fakes.Licenses.ByEmail["jared@example.com"] = new LicenseRecord
        {
            Id = "lic_1",
            Email = "jared@example.com",
            Sku = LicenseSkus.Boltmate,
            Tier = LicenseTier.Boltmate,
            Status = "active",
            StripeCheckoutSessionId = "sess_1",
            StripePaymentIntentId = "pi_1",
        };
        var before = fakes.Licenses.ByEmail["jared@example.com"];
        before.Status = "active";

        await handler.DispatchAsync(CheckoutEvent("sess_1", "pi_1", "jared@example.com"));

        // No mutation — same record, same fields.
        var after = fakes.Licenses.ByEmail["jared@example.com"];
        Assert.Same(before, after);
        Assert.Equal("sess_1", after.StripeCheckoutSessionId);
    }

    [Fact]
    public async Task Checkout_completed_with_no_prior_license_creates_boltmate()
    {
        var (handler, fakes) = Build();

        await handler.DispatchAsync(CheckoutEvent("sess_fresh", "pi_fresh", "fresh@example.com"));

        var rec = fakes.Licenses.ByEmail["fresh@example.com"];
        Assert.Equal(LicenseTier.Boltmate, rec.Tier);
        Assert.Null(rec.ExpiresAt);
        Assert.Equal("sess_fresh", rec.StripeCheckoutSessionId);
    }

    [Fact]
    public async Task Refund_revokes_matching_license()
    {
        var (handler, fakes) = Build();
        fakes.Licenses.ByEmail["jared@example.com"] = new LicenseRecord
        {
            Id = "lic_paid",
            Email = "jared@example.com",
            Sku = LicenseSkus.Boltmate,
            Tier = LicenseTier.Boltmate,
            Status = "active",
            StripePaymentIntentId = "pi_paid",
        };

        await handler.DispatchAsync(ChargeRefundedEvent("ch_1", "pi_paid"));

        var rec = fakes.Licenses.ByEmail["jared@example.com"];
        Assert.Equal("revoked", rec.Status);
        Assert.Equal(LicenseTier.Trial, rec.Tier);
        Assert.NotNull(rec.RevokedAt);
        Assert.Equal(rec.RevokedAt, rec.ExpiresAt);
    }

    [Fact]
    public async Task Refund_redelivery_is_idempotent()
    {
        var (handler, fakes) = Build();
        var revokedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        fakes.Licenses.ByEmail["jared@example.com"] = new LicenseRecord
        {
            Id = "lic_paid",
            Email = "jared@example.com",
            Sku = LicenseSkus.Boltmate,
            Tier = LicenseTier.Trial,
            Status = "revoked",
            StripePaymentIntentId = "pi_paid",
            RevokedAt = revokedAt,
            ExpiresAt = revokedAt,
        };

        await handler.DispatchAsync(ChargeRefundedEvent("ch_2", "pi_paid"));

        // RevokedAt unchanged — second delivery did NOT re-stamp the timestamp.
        Assert.Equal(revokedAt, fakes.Licenses.ByEmail["jared@example.com"].RevokedAt);
    }

    [Fact]
    public async Task Refund_with_no_matching_license_is_noop()
    {
        var (handler, fakes) = Build();

        await handler.DispatchAsync(ChargeRefundedEvent("ch_x", "pi_unknown"));

        Assert.Empty(fakes.Licenses.ByEmail);
    }

    private static (StripeWebhookHandler Handler, Fakes Fakes) Build()
    {
        var fakes = new Fakes();
        var handler = new StripeWebhookHandler(
            Options.Create(new LicenseApiOptions { StripeWebhookSecret = "whsec_test" }),
            fakes.Licenses,
            fakes.Emails,
            NullLogger<StripeWebhookHandler>.Instance);
        return (handler, fakes);
    }

    private static Event CheckoutEvent(string sessionId, string paymentIntentId, string email)
    {
        return new Event
        {
            Type = "checkout.session.completed",
            Data = new EventData
            {
                Object = new Session
                {
                    Id = sessionId,
                    PaymentIntentId = paymentIntentId,
                    CustomerEmail = email,
                    Metadata = new Dictionary<string, string>(),
                }
            }
        };
    }

    private static Event ChargeRefundedEvent(string chargeId, string paymentIntentId)
    {
        return new Event
        {
            Type = "charge.refunded",
            Data = new EventData
            {
                Object = new Charge
                {
                    Id = chargeId,
                    PaymentIntentId = paymentIntentId,
                }
            }
        };
    }

    internal sealed class Fakes
    {
        public FakeLicenseRepository Licenses { get; } = new();
        public FakeEmailNotifier Emails { get; } = new();
    }
}
