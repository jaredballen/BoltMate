using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.LicenseApi.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoltMate.LicenseApi.Services;

internal sealed class GitHubDispatcher : IGitHubDispatcher
{
    private static readonly HttpClient Http = CreateClient();
    private readonly LicenseApiOptions _options;
    private readonly ILogger<GitHubDispatcher> _log;

    public GitHubDispatcher(IOptions<LicenseApiOptions> options, ILogger<GitHubDispatcher> log)
    {
        _options = options.Value;
        _log = log;
    }

    public async Task DispatchAsync(string eventType, object? clientPayload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.GitHubRepoOwner)
            || string.IsNullOrWhiteSpace(_options.GitHubRepoName)
            || string.IsNullOrWhiteSpace(_options.GitHubPat))
        {
            _log.LogWarning("GitHub dispatcher not configured; skipping {EventType}.", eventType);
            return;
        }

        var url = $"https://api.github.com/repos/{_options.GitHubRepoOwner}/{_options.GitHubRepoName}/dispatches";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new
            {
                event_type = eventType,
                client_payload = clientPayload,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.GitHubPat);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        try
        {
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _log.LogWarning("GitHub dispatch failed for {EventType}: {Status} {Body}",
                    eventType, (int)resp.StatusCode, body);
            }
            else
            {
                _log.LogInformation("GitHub dispatch sent: {EventType}.", eventType);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GitHub dispatch {EventType} threw.", eventType);
        }
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient();
        // GitHub API requires a real UA — bare "Mozilla/5.0" or even an
        // empty UA is rejected. They check for SOMETHING here.
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BoltMate-LicenseApi", "1.0"));
        return c;
    }
}
