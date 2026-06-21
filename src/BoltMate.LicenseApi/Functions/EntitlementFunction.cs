using System;
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

namespace BoltMate.LicenseApi.Functions;

public sealed class EntitlementFunction
{
    private readonly IIdTokenValidator _idTokens;
    private readonly ILicenseRepository _licenses;
    private readonly IRefreshLogRepository _refreshLog;
    private readonly IRateLimiter _rateLimiter;
    private readonly IJwtSigner _signer;
    private readonly LicenseApiOptions _options;
    private readonly ILogger<EntitlementFunction> _log;

    public EntitlementFunction(
        IIdTokenValidator idTokens,
        ILicenseRepository licenses,
        IRefreshLogRepository refreshLog,
        IRateLimiter rateLimiter,
        IJwtSigner signer,
        IOptions<LicenseApiOptions> options,
        ILogger<EntitlementFunction> log)
    {
        _idTokens = idTokens;
        _licenses = licenses;
        _refreshLog = refreshLog;
        _rateLimiter = rateLimiter;
        _signer = signer;
        _options = options.Value;
        _log = log;
    }

    [Function("Entitlement")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "entitlement")] HttpRequest req,
        CancellationToken ct)
    {
        var authHeader = (string?)req.Headers.Authorization;
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Error(HttpStatusCode.Unauthorized, EntitlementErrorCodes.InvalidIdToken, "missing bearer token");

        var idToken = authHeader["Bearer ".Length..].Trim();
        var validated = await _idTokens.ValidateAsync(idToken, ct).ConfigureAwait(false);
        if (validated is null)
            return Error(HttpStatusCode.Unauthorized, EntitlementErrorCodes.InvalidIdToken, "id token invalid");

        var license = await _licenses.GetByEmailAsync(validated.Email, ct).ConfigureAwait(false);
        if (license is null)
            return Error(HttpStatusCode.NotFound, EntitlementErrorCodes.LicenseNotFound, "no active license for this account");

        if (license.Status == "revoked")
            return Error(HttpStatusCode.Forbidden, EntitlementErrorCodes.LicenseRevoked, "license revoked");

        var limit = await _rateLimiter.CheckAsync(license.Id, ct).ConfigureAwait(false);
        if (!limit.Allowed)
            return Error(HttpStatusCode.TooManyRequests, EntitlementErrorCodes.RateLimited, limit.Reason, limit.RetryAfterSeconds);

        await _licenses.BindOAuthSubjectAsync(license.Id, license.Email, validated.Subject, ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var claims = new LicenseClaims(
            Subject: validated.Subject,
            Email: license.Email,
            LicenseId: license.Id,
            Sku: license.Sku,
            Tier: license.Tier,
            Issuer: _options.Issuer,
            IssuedAt: now,
            ExpiresAt: now.AddDays(_options.RefreshTokenLifetimeDays));

        var jwt = await _signer.SignAsync(claims, ct).ConfigureAwait(false);
        await _refreshLog.RecordAsync(license.Id, validated.Subject, now, ct).ConfigureAwait(false);

        return new OkObjectResult(new EntitlementResponse(jwt, claims.ExpiresAt.AddDays(-3).ToUnixTimeSeconds()));
    }

    private static IActionResult Error(HttpStatusCode status, string code, string? message, int? retryAfterSeconds = null)
    {
        return new ObjectResult(new EntitlementErrorResponse(code, message, retryAfterSeconds))
        {
            StatusCode = (int)status
        };
    }
}
