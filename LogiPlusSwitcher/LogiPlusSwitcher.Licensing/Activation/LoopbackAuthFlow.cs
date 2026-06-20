using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace LogiPlusSwitcher.Licensing.Activation;

internal sealed class LoopbackAuthFlow : IBrowserAuthFlow
{
    private readonly HttpClient _http;
    private readonly LoopbackAuthOptions _options;

    public LoopbackAuthFlow(HttpClient http, LoopbackAuthOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<AuthFlowResult> AuthenticateAsync(CancellationToken ct = default)
    {
        var verifier = GeneratePkceVerifier();
        var challenge = ComputePkceChallenge(verifier);
        var state = GenerateRandomState();

        var port = ReserveFreeLoopbackPort();
        var redirectUri = $"http://127.0.0.1:{port}/{_options.RedirectPath.TrimStart('/')}";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var authUrl = BuildAuthorizeUrl(redirectUri, challenge, state);
        OpenSystemBrowser(authUrl);

        var (code, returnedState) = await WaitForCallbackAsync(listener, ct).ConfigureAwait(false);
        listener.Stop();

        if (!string.Equals(state, returnedState, StringComparison.Ordinal))
            throw new AuthFlowException("OAuth state mismatch — possible CSRF.");

        return await ExchangeCodeAsync(code, redirectUri, verifier, ct).ConfigureAwait(false);
    }

    private string BuildAuthorizeUrl(string redirectUri, string codeChallenge, string state)
    {
        var qs = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = string.Join(' ', _options.Scopes),
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };
        var sb = new StringBuilder(_options.AuthorizeEndpoint.ToString());
        sb.Append(_options.AuthorizeEndpoint.Query.Length == 0 ? '?' : '&');
        var first = true;
        foreach (var kv in qs)
        {
            if (!first) sb.Append('&');
            sb.Append(HttpUtility.UrlEncode(kv.Key));
            sb.Append('=');
            sb.Append(HttpUtility.UrlEncode(kv.Value));
            first = false;
        }
        return sb.ToString();
    }

    private static async Task<(string code, string state)> WaitForCallbackAsync(HttpListener listener, CancellationToken ct)
    {
        using var reg = ct.Register(listener.Stop);

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().ConfigureAwait(false);
        }
        catch (HttpListenerException ex) when (ct.IsCancellationRequested)
        {
            throw new AuthFlowException("OAuth callback cancelled.", ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new AuthFlowException("OAuth listener disposed before callback.", ex);
        }

        var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
        var code = query["code"];
        var state = query["state"];
        var error = query["error"];

        await WriteResponseAsync(context, error is null
            ? "<html><body><h2>Sign-in complete</h2><p>You can close this window.</p></body></html>"
            : $"<html><body><h2>Sign-in failed</h2><p>{HttpUtility.HtmlEncode(error)}</p></body></html>")
            .ConfigureAwait(false);

        if (error is not null)
            throw new AuthFlowException($"OAuth provider returned error: {error}");
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            throw new AuthFlowException("OAuth callback missing code/state.");

        return (code, state);
    }

    private static async Task WriteResponseAsync(HttpListenerContext context, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.OutputStream.Close();
    }

    private async Task<AuthFlowResult> ExchangeCodeAsync(string code, string redirectUri, string verifier, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = _options.ClientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier
        });

        using var resp = await _http.PostAsync(_options.TokenEndpoint, form, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new AuthFlowException($"Token exchange failed: {(int)resp.StatusCode} {body}");
        }

        var token = await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct).ConfigureAwait(false);
        if (token is null || string.IsNullOrEmpty(token.IdToken))
            throw new AuthFlowException("Token response missing id_token.");

        return new AuthFlowResult(token.IdToken, token.RefreshToken);
    }

    private static int ReserveFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string GeneratePkceVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    private static string ComputePkceChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string GenerateRandomState()
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void OpenSystemBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new AuthFlowException($"Failed to open system browser for {url}.", ex);
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("id_token")] string IdToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn);
}

public sealed record LoopbackAuthOptions(
    Uri AuthorizeEndpoint,
    Uri TokenEndpoint,
    string ClientId,
    IReadOnlyList<string> Scopes,
    string RedirectPath = "callback");
