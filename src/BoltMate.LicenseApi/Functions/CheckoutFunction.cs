using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.LicenseApi.Configuration;
using BoltMate.LicenseApi.Services;
using BoltMate.Licensing.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace BoltMate.LicenseApi.Functions;

/// <summary>
/// Mints a Stripe Checkout Session for the authenticated user and
/// returns the redirect URL.
/// </summary>
/// <remarks>
/// Caller MUST present a valid Bearer ID token. The session is bound
/// to the email Microsoft Entra External ID stamped into the token, so
/// the eventual <c>checkout.session.completed</c> webhook can route
/// the upgrade to the correct license row by email. Metadata carries
/// the SKU (<see cref="LicenseSkus.Boltmate"/>) so the webhook handler
/// can route on it instead of relying on the Price.
/// </remarks>
public sealed class CheckoutFunction
{
    private readonly IIdTokenValidator _idTokens;
    private readonly LicenseApiOptions _options;
    private readonly ILogger<CheckoutFunction> _log;

    public CheckoutFunction(
        IIdTokenValidator idTokens,
        IOptions<LicenseApiOptions> options,
        ILogger<CheckoutFunction> log)
    {
        _idTokens = idTokens;
        _options = options.Value;
        _log = log;
    }

    [Function("Checkout")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "checkout")] HttpRequest req,
        CancellationToken ct)
    {
        var authHeader = (string?)req.Headers.Authorization;
        if (string.IsNullOrWhiteSpace(authHeader)
            || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Error(HttpStatusCode.Unauthorized, "missing bearer token");

        var idToken = authHeader["Bearer ".Length..].Trim();
        var validated = await _idTokens.ValidateAsync(idToken, ct).ConfigureAwait(false);
        if (validated is null)
            return Error(HttpStatusCode.Unauthorized, "id token invalid");

        try
        {
            // Resolve the active Price from its stable lookup key. Same
            // mechanic the site uses at build time — but here we query
            // it server-side so the Price ID never has to land in the
            // page bundle. A live-mode swap doesn't break us either.
            var priceService = new PriceService(BuildStripeClient());
            var prices = await priceService.ListAsync(
                new PriceListOptions
                {
                    LookupKeys = new() { _options.StripePriceLookupKey },
                    Active = true,
                    Limit = 1,
                },
                cancellationToken: ct).ConfigureAwait(false);

            var price = prices.Data.FirstOrDefault();
            if (price is null)
            {
                _log.LogError("No active Stripe Price for lookup key {Key}.",
                    _options.StripePriceLookupKey);
                return Error(HttpStatusCode.InternalServerError, "checkout misconfigured");
            }

            var origin = _options.SiteOrigin.TrimEnd('/');
            var sessionService = new SessionService(BuildStripeClient());
            var session = await sessionService.CreateAsync(
                new SessionCreateOptions
                {
                    Mode = "payment",
                    PaymentMethodTypes = new() { "card" },
                    LineItems = new()
                    {
                        new SessionLineItemOptions { Price = price.Id, Quantity = 1 }
                    },
                    CustomerEmail = validated.Email,
                    SuccessUrl = $"{origin}/account?checkout=success&session_id={{CHECKOUT_SESSION_ID}}",
                    CancelUrl = $"{origin}/pricing?checkout=cancelled",
                    AllowPromotionCodes = false,
                    Metadata = new()
                    {
                        ["sku"] = LicenseSkus.Boltmate,
                        ["oauth_sub"] = validated.Subject,
                    },
                },
                cancellationToken: ct).ConfigureAwait(false);

            _log.LogInformation("Created checkout session {SessionId} for {Email}.",
                session.Id, validated.Email);

            return new OkObjectResult(new CheckoutResponse(session.Url, session.Id));
        }
        catch (StripeException ex)
        {
            _log.LogError(ex, "Stripe checkout creation failed for {Email}.", validated.Email);
            return Error(HttpStatusCode.BadGateway, "payment provider error");
        }
    }

    private StripeClient BuildStripeClient() => new(_options.StripeSecretKey);

    private static IActionResult Error(HttpStatusCode status, string message)
        => new ObjectResult(new { error = message }) { StatusCode = (int)status };
}

public sealed record CheckoutResponse(string Url, string SessionId);
