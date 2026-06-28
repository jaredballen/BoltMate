using System;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.LicenseApi.Configuration;
using BoltMate.LicenseApi.Models;
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
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

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

        var body = await TryReadBodyAsync(req, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        var license = await _licenses.GetByEmailAsync(validated.Email, ct).ConfigureAwait(false);

        // No license for this email → auto-provision a Trial subject to
        // the hardware-reuse block. We only enforce the block when the
        // client supplied a HardwareIdHash; a missing hash means we
        // can't tell if it's the same machine, so we err on the side of
        // letting them in (real users may legitimately not send one;
        // attackers would have to actively strip the field, and there's
        // still the rate limiter + paid-conversion check upstream).
        if (license is null)
        {
            if (!string.IsNullOrWhiteSpace(body?.HardwareIdHash))
            {
                var cutoff = now.AddDays(-_options.TrialReuseBlockDays);
                var reused = await _licenses
                    .HasRecentTrialForHardwareAsync(body.HardwareIdHash, cutoff, ct)
                    .ConfigureAwait(false);
                if (reused)
                {
                    _log.LogInformation("Trial reused for hw={HardwareIdHash}, email={Email}.",
                        body.HardwareIdHash, validated.Email);
                    return Error(HttpStatusCode.Forbidden, EntitlementErrorCodes.TrialReused,
                        "this hardware already used its trial — purchase a license to continue");
                }
            }

            license = await ProvisionTrialAsync(validated.Email, validated.Subject, body?.HardwareIdHash, now, ct).ConfigureAwait(false);
            _log.LogInformation("Provisioned Trial {LicenseId} for {Email}.", license.Id, license.Email);
        }
        else if (license.Status == "revoked")
        {
            return Error(HttpStatusCode.Forbidden, EntitlementErrorCodes.LicenseRevoked, "license revoked");
        }

        var limit = await _rateLimiter.CheckAsync(license.Id, ct).ConfigureAwait(false);
        if (!limit.Allowed)
            return Error(HttpStatusCode.TooManyRequests, EntitlementErrorCodes.RateLimited, limit.Reason, limit.RetryAfterSeconds);

        await _licenses.BindOAuthSubjectAsync(license.Id, license.Email, validated.Subject, ct).ConfigureAwait(false);

        // JWT lifetime: capped by the license's own ExpiresAt (Trial)
        // so we never hand out a 30-day token to a 14-day Trial.
        var jwtExpiry = now.AddDays(_options.RefreshTokenLifetimeDays);
        if (license.ExpiresAt is { } licExpires && licExpires < jwtExpiry)
            jwtExpiry = licExpires;

        var claims = new LicenseClaims(
            Subject: validated.Subject,
            Email: license.Email,
            LicenseId: license.Id,
            Sku: license.Sku,
            Tier: license.Tier,
            Issuer: _options.Issuer,
            IssuedAt: now,
            ExpiresAt: jwtExpiry);

        var jwt = await _signer.SignAsync(claims, ct).ConfigureAwait(false);
        await _refreshLog.RecordAsync(license.Id, validated.Subject, now, ct).ConfigureAwait(false);

        // Lazy-fill: if an account predates the SyncKey field, mint
        // one on the next entitlement hit so existing users get peer
        // crypto automatically. Cheap (32 random bytes + one upsert).
        if (string.IsNullOrEmpty(license.SyncKeyBase64))
        {
            license.SyncKeyBase64 = NewSyncKey();
            await _licenses.UpsertAsync(license, ct).ConfigureAwait(false);
            _log.LogInformation("Backfilled SyncKey for {LicenseId}.", license.Id);
        }

        return new OkObjectResult(new EntitlementResponse(
            jwt,
            claims.ExpiresAt.AddDays(-3).ToUnixTimeSeconds(),
            license.SyncKeyBase64));
    }

    private async Task<LicenseRecord> ProvisionTrialAsync(
        string email, string oauthSubject, string? hardwareIdHash, DateTimeOffset now, CancellationToken ct)
    {
        var record = new LicenseRecord
        {
            Id = $"lic_{Guid.NewGuid():N}",
            Email = email,
            OAuthSubject = oauthSubject,
            Sku = LicenseSkus.Boltmate,
            Tier = LicenseTier.Trial,
            Status = "active",
            IssuedAt = now,
            ExpiresAt = now.AddDays(_options.TrialLengthDays),
            HardwareIdHash = hardwareIdHash,
            TrialOriginAt = now,
            SyncKeyBase64 = NewSyncKey(),
        };
        await _licenses.UpsertAsync(record, ct).ConfigureAwait(false);
        return record;
    }

    private static string NewSyncKey()
    {
        // AES-256 key for the peer-message envelope. 32 random bytes
        // from a CSPRNG — never derived from anything user-controlled
        // because compromised email-recovery wouldn't grant LAN trust
        // that way.
        var bytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static async Task<EntitlementRequest?> TryReadBodyAsync(HttpRequest req, CancellationToken ct)
    {
        if (req.ContentLength is null or 0) return null;
        try
        {
            return await JsonSerializer.DeserializeAsync<EntitlementRequest>(req.Body, JsonOpts, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IActionResult Error(HttpStatusCode status, string code, string? message, int? retryAfterSeconds = null)
    {
        return new ObjectResult(new EntitlementErrorResponse(code, message, retryAfterSeconds))
        {
            StatusCode = (int)status
        };
    }
}
