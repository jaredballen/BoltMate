using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using LogiPlusSwitcher.LicenseApi.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogiPlusSwitcher.LicenseApi.Services;

internal sealed class ResendSupportTicketSink : ISupportTicketSink
{
    private static readonly HttpClient Http = new();
    private readonly LicenseApiOptions _options;
    private readonly ILogger<ResendSupportTicketSink> _log;

    public ResendSupportTicketSink(IOptions<LicenseApiOptions> options, ILogger<ResendSupportTicketSink> log)
    {
        _options = options.Value;
        _log = log;
    }

    public async Task SubmitAsync(SupportTicket ticket, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ResendApiKey))
        {
            _log.LogWarning("Resend API key not configured; logging ticket instead. From={From} Subject={Subject}", ticket.FromEmail, ticket.Subject);
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = JsonContent.Create(new
            {
                from = $"LogiPlus Switcher Support <{_options.SupportEmailTo}>",
                to = new[] { _options.SupportEmailTo },
                reply_to = ticket.FromEmail,
                subject = $"[Support] {ticket.Subject}",
                text = BuildBody(ticket)
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ResendApiKey);

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    private static string BuildBody(SupportTicket t) =>
        $"From: {t.FromName ?? "(no name)"} <{t.FromEmail}>\nLicense: {t.LicenseId ?? "(none)"}\n\n{t.Body}";
}
