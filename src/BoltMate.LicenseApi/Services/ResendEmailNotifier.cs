using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.LicenseApi.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoltMate.LicenseApi.Services;

internal sealed class ResendEmailNotifier : IEmailNotifier
{
    private static readonly HttpClient Http = new();
    private readonly LicenseApiOptions _options;
    private readonly ILogger<ResendEmailNotifier> _log;

    public ResendEmailNotifier(IOptions<LicenseApiOptions> options, ILogger<ResendEmailNotifier> log)
    {
        _options = options.Value;
        _log = log;
    }

    public Task PurchaseConfirmationAsync(string toEmail, string licenseId, CancellationToken ct = default)
    {
        var html = BuildHtml(
            heading: "You're all set.",
            body: $"<p>Thanks for buying BoltMate.</p>" +
                  $"<p>Your lifetime license <code>{Html(licenseId)}</code> is now active. " +
                  $"Sign in inside the desktop app with this same email " +
                  $"(<strong>{Html(toEmail)}</strong>) on every machine you use — " +
                  $"the license activates automatically.</p>" +
                  $"<p>Need installers? <a href=\"{_options.SiteOrigin}/account\">Your account page</a> has the download links.</p>",
            cta: ("Download installers", $"{_options.SiteOrigin}/account"));
        return SendAsync(toEmail, "Your BoltMate license is active", html, ct);
    }

    public Task TrialEndingAsync(string toEmail, int daysLeft, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        var when = daysLeft <= 1 ? "tomorrow" : $"in {daysLeft} days";
        var html = BuildHtml(
            heading: $"Your trial ends {when}.",
            body: $"<p>Your BoltMate trial expires on <strong>{expiresAt:MMMM d}</strong>.</p>" +
                  $"<p>Lifetime license is a one-time <strong>$14.99</strong> — no subscription, every future version included.</p>",
            cta: ("Buy lifetime — $14.99", $"{_options.SiteOrigin}/checkout"));
        return SendAsync(toEmail, $"BoltMate trial — {daysLeft} day{(daysLeft == 1 ? "" : "s")} left", html, ct);
    }

    public Task TrialExpiredAsync(string toEmail, CancellationToken ct = default)
    {
        var html = BuildHtml(
            heading: "Your trial just ended.",
            body: "<p>BoltMate's cross-machine sync paused on every machine you signed into.</p>" +
                  "<p>Buying lifetime ($14.99, one-time) flips it back on instantly — same email, no reconfiguration.</p>",
            cta: ("Buy lifetime — $14.99", $"{_options.SiteOrigin}/checkout"));
        return SendAsync(toEmail, "BoltMate trial ended", html, ct);
    }

    public Task SupportTicketAckAsync(string toEmail, string subject, CancellationToken ct = default)
    {
        var html = BuildHtml(
            heading: "Got your support request.",
            body: $"<p>We received: <em>{Html(subject)}</em>.</p>" +
                  $"<p>Reply to this email with anything else we should know — that's the fastest channel. " +
                  $"A human will get back to you within one business day.</p>",
            cta: null);
        return SendAsync(toEmail, "Got your support request", html, ct);
    }

    private async Task SendAsync(string toEmail, string subject, string html, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ResendApiKey))
        {
            _log.LogWarning("Resend API key not configured; would have sent To={To} Subject='{Subject}'.",
                toEmail, subject);
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = JsonContent.Create(new
            {
                from = "BoltMate <hello@boltmate.app>",
                to = new[] { toEmail },
                subject,
                html,
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ResendApiKey);

        try
        {
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _log.LogWarning("Resend send failed for {To} ({Subject}): {Status} {Body}",
                    toEmail, subject, (int)resp.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            // Notifier failures must not break the upstream webhook /
            // function — Stripe + the user already got their answer.
            _log.LogWarning(ex, "Resend send threw for {To} ({Subject})", toEmail, subject);
        }
    }

    private static string BuildHtml(string heading, string body, (string Text, string Url)? cta)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><body style=\"font-family:-apple-system,BlinkMacSystemFont,Segoe UI,Roboto,sans-serif;background:#0a0a0c;color:#eaeaec;margin:0;padding:24px;\">");
        sb.AppendLine("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:520px;margin:0 auto;background:#141417;border:1px solid #2a2a30;border-radius:14px;padding:32px;\">");
        sb.Append("<tr><td>");
        sb.Append("<h1 style=\"font-size:22px;margin:0 0 16px;color:#eaeaec;\">").Append(Html(heading)).AppendLine("</h1>");
        sb.AppendLine(body);
        if (cta is { } c)
        {
            sb.Append("<p style=\"margin-top:24px;\"><a href=\"").Append(Html(c.Url))
              .Append("\" style=\"display:inline-block;background:#5cd09a;color:#0a0a0c;padding:12px 22px;border-radius:8px;text-decoration:none;font-weight:600;\">")
              .Append(Html(c.Text)).AppendLine("</a></p>");
        }
        sb.AppendLine("<p style=\"margin-top:28px;font-size:13px;color:#6a6a72;\">BoltMate — companion to Logi Options+</p>");
        sb.AppendLine("</td></tr></table></body></html>");
        return sb.ToString();
    }

    private static string Html(string s) => System.Net.WebUtility.HtmlEncode(s);
}
