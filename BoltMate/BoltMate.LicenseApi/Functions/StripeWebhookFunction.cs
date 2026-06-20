using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.LicenseApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Stripe;

namespace BoltMate.LicenseApi.Functions;

public sealed class StripeWebhookFunction
{
    private readonly IStripeWebhookHandler _handler;
    private readonly ILogger<StripeWebhookFunction> _log;

    public StripeWebhookFunction(IStripeWebhookHandler handler, ILogger<StripeWebhookFunction> log)
    {
        _handler = handler;
        _log = log;
    }

    [Function("StripeWebhook")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "stripe-webhook")] HttpRequest req,
        CancellationToken ct)
    {
        using var reader = new StreamReader(req.Body);
        var payload = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        var signature = (string?)req.Headers["Stripe-Signature"];
        if (string.IsNullOrWhiteSpace(signature))
            return new BadRequestObjectResult(new { error = "missing Stripe-Signature header" });

        try
        {
            await _handler.HandleAsync(payload, signature, ct).ConfigureAwait(false);
            return new OkResult();
        }
        catch (StripeException ex)
        {
            _log.LogWarning(ex, "Stripe webhook validation failed.");
            return new BadRequestObjectResult(new { error = "invalid signature" });
        }
    }
}
