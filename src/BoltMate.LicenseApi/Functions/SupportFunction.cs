using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.LicenseApi.Configuration;
using BoltMate.LicenseApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoltMate.LicenseApi.Functions;

/// <summary>
/// Accepts a support ticket either as JSON (site form) or
/// multipart/form-data (in-app "Send logs" — body fields + a zip part).
///
/// <para>Authentication is optional. When the caller supplies a Bearer
/// ID token, we validate it and use the token's email regardless of any
/// submitted Email field. Anonymous callers MUST include an Email field
/// — used by the site's anonymous /support form.</para>
/// </summary>
public sealed class SupportFunction
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IIdTokenValidator _idTokens;
    private readonly ISupportBundleStore _bundles;
    private readonly ISupportTicketSink _sink;
    private readonly IEmailNotifier _notifier;
    private readonly LicenseApiOptions _options;
    private readonly ILogger<SupportFunction> _log;

    public SupportFunction(
        IIdTokenValidator idTokens,
        ISupportBundleStore bundles,
        ISupportTicketSink sink,
        IEmailNotifier notifier,
        IOptions<LicenseApiOptions> options,
        ILogger<SupportFunction> log)
    {
        _idTokens = idTokens;
        _bundles = bundles;
        _sink = sink;
        _notifier = notifier;
        _options = options.Value;
        _log = log;
    }

    [Function("Support")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "support")] HttpRequest req,
        CancellationToken ct)
    {
        // Optional Bearer — if present and valid, overrides the submitted
        // email. Lets logged-in app users skip the email field entirely.
        string? authedEmail = null;
        string? authedSub = null;
        var authHeader = (string?)req.Headers.Authorization;
        if (!string.IsNullOrWhiteSpace(authHeader)
            && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var idToken = authHeader["Bearer ".Length..].Trim();
            var validated = await _idTokens.ValidateAsync(idToken, ct).ConfigureAwait(false);
            if (validated is not null)
            {
                authedEmail = validated.Email;
                authedSub = validated.Subject;
            }
        }

        var maxBytes = (long)_options.SupportBundleMaxSizeMB * 1024 * 1024;
        if (req.ContentLength is { } cl && cl > maxBytes)
            return Error(HttpStatusCode.RequestEntityTooLarge, "bundle exceeds size limit");

        SupportSubmission submission;
        SupportBundleUpload? upload = null;

        if (req.HasFormContentType)
        {
            var form = await req.ReadFormAsync(ct).ConfigureAwait(false);
            submission = new SupportSubmission(
                Email: form["email"].FirstOrDefault(),
                Name: form["name"].FirstOrDefault(),
                Subject: form["subject"].FirstOrDefault(),
                Message: form["message"].FirstOrDefault(),
                LicenseId: form["licenseId"].FirstOrDefault(),
                Source: form["source"].FirstOrDefault());

            var bundle = form.Files.GetFile("bundle") ?? form.Files.FirstOrDefault();
            if (bundle is { Length: > 0 })
            {
                if (bundle.Length > maxBytes)
                    return Error(HttpStatusCode.RequestEntityTooLarge, "bundle exceeds size limit");
                await using var s = bundle.OpenReadStream();
                upload = await _bundles.UploadAsync(
                    s,
                    bundle.FileName,
                    string.IsNullOrWhiteSpace(bundle.ContentType) ? "application/zip" : bundle.ContentType,
                    ct).ConfigureAwait(false);
            }
        }
        else
        {
            // JSON path (site form).
            SupportSubmission? body = null;
            try
            {
                body = await JsonSerializer.DeserializeAsync<SupportSubmission>(req.Body, JsonOpts, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Malformed JSON on /support.");
            }
            if (body is null)
                return Error(HttpStatusCode.BadRequest, "expected JSON body or multipart/form-data");
            submission = body;
        }

        var email = authedEmail ?? submission.Email;
        if (string.IsNullOrWhiteSpace(email))
            return Error(HttpStatusCode.BadRequest, "email required for anonymous submissions");
        if (string.IsNullOrWhiteSpace(submission.Message))
            return Error(HttpStatusCode.BadRequest, "message required");

        var ticket = new SupportTicket(
            FromEmail: email!,
            FromName: submission.Name,
            Subject: submission.Subject ?? "(no subject)",
            Body: submission.Message!,
            LicenseId: submission.LicenseId,
            BundleUrl: upload?.DownloadUrl,
            BundleSizeBytes: upload?.SizeBytes,
            Source: submission.Source ?? (authedSub is null ? "anonymous" : "authenticated"));

        await _sink.SubmitAsync(ticket, ct).ConfigureAwait(false);
        // Auto-ack to the submitter so they see "got it" instead of
        // wondering whether the form worked. Fire-and-forget — sink
        // already succeeded; ack failure is logged but not surfaced.
        await _notifier.SupportTicketAckAsync(email!, ticket.Subject, ct).ConfigureAwait(false);
        return new AcceptedResult();
    }

    private static IActionResult Error(HttpStatusCode status, string message)
    {
        return new ObjectResult(new { error = message })
        {
            StatusCode = (int)status
        };
    }

    private sealed record SupportSubmission(
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("licenseId")] string? LicenseId,
        [property: JsonPropertyName("source")] string? Source);
}
