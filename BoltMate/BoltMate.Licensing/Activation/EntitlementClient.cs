using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.Licensing.Contracts;

namespace BoltMate.Licensing.Activation;

internal sealed class EntitlementClient : IEntitlementClient
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;

    public EntitlementClient(HttpClient http, Uri endpoint)
    {
        _http = http;
        _endpoint = endpoint;
    }

    public async Task<EntitlementResponse> RequestEntitlementAsync(string idToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idToken);

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
        req.Content = JsonContent.Create(new { });

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (resp.IsSuccessStatusCode)
        {
            var ok = await resp.Content.ReadFromJsonAsync<EntitlementResponse>(cancellationToken: ct).ConfigureAwait(false);
            if (ok is null || string.IsNullOrEmpty(ok.Jwt))
                throw new EntitlementRequestException("malformed_response", "empty body", null);
            return ok;
        }

        EntitlementErrorResponse? err = null;
        try
        {
            err = await resp.Content.ReadFromJsonAsync<EntitlementErrorResponse>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch
        {
        }

        var code = err?.Error ?? resp.StatusCode.ToString();
        var message = err?.Message;
        var retry = err?.RetryAfterSeconds
            ?? (int?)resp.Headers.RetryAfter?.Delta?.TotalSeconds;

        throw new EntitlementRequestException(code, message, retry);
    }
}
