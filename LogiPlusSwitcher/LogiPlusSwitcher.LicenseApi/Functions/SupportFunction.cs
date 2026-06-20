using System.Threading;
using System.Threading.Tasks;
using LogiPlusSwitcher.LicenseApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LogiPlusSwitcher.LicenseApi.Functions;

public sealed class SupportFunction
{
    private readonly ISupportTicketSink _sink;
    private readonly ILogger<SupportFunction> _log;

    public SupportFunction(ISupportTicketSink sink, ILogger<SupportFunction> log)
    {
        _sink = sink;
        _log = log;
    }

    [Function("Support")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "support")] HttpRequest req,
        [FromBody] SupportRequest body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Subject) || string.IsNullOrWhiteSpace(body.Message))
            return new BadRequestObjectResult(new { error = "email, subject, message are required" });

        await _sink.SubmitAsync(new SupportTicket(body.Email, body.Name, body.Subject, body.Message, body.LicenseId), ct).ConfigureAwait(false);
        return new AcceptedResult();
    }

    public sealed record SupportRequest(string Email, string? Name, string Subject, string Message, string? LicenseId);
}
