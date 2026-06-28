using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.LicenseApi.Models;
using BoltMate.LicenseApi.Services;
using BoltMate.Licensing.Contracts;

namespace BoltMate.Licensing.Tests;

internal sealed class FakeLicenseRepository : ILicenseRepository
{
    public Dictionary<string, LicenseRecord> ByEmail { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Hash, DateTimeOffset Cutoff)> HardwareReuseChecks { get; } = new();
    public bool BlockHardwareReuse { get; set; }

    public Task<LicenseRecord?> GetByEmailAsync(string email, CancellationToken ct = default)
        => Task.FromResult(ByEmail.TryGetValue(email, out var r) ? r : null);

    public Task<IReadOnlyList<LicenseRecord>> ListByEmailAsync(string email, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LicenseRecord>>(
            ByEmail.TryGetValue(email, out var r) ? new[] { r } : Array.Empty<LicenseRecord>());

    public Task UpsertAsync(LicenseRecord record, CancellationToken ct = default)
    {
        record.PartitionKey = record.Email.ToLowerInvariant();
        ByEmail[record.Email] = record;
        return Task.CompletedTask;
    }

    public Task BindOAuthSubjectAsync(string licenseId, string email, string oauthSubject, CancellationToken ct = default)
    {
        if (ByEmail.TryGetValue(email, out var r)) r.OAuthSubject = oauthSubject;
        return Task.CompletedTask;
    }

    public Task<bool> HasRecentTrialForHardwareAsync(string hardwareIdHash, DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        HardwareReuseChecks.Add((hardwareIdHash, cutoffUtc));
        if (BlockHardwareReuse) return Task.FromResult(true);
        var hit = ByEmail.Values.Any(r =>
            r.HardwareIdHash == hardwareIdHash
            && r.TrialOriginAt is { } origin && origin >= cutoffUtc);
        return Task.FromResult(hit);
    }

    public Task<LicenseRecord?> GetByStripePaymentIntentIdAsync(string paymentIntentId, CancellationToken ct = default)
    {
        var hit = ByEmail.Values.FirstOrDefault(r => r.StripePaymentIntentId == paymentIntentId);
        return Task.FromResult<LicenseRecord?>(hit);
    }

    public Task<int> DeleteByEmailAsync(string email, CancellationToken ct = default)
    {
        if (!ByEmail.Remove(email)) return Task.FromResult(0);
        return Task.FromResult(1);
    }

    public Task<IReadOnlyList<LicenseRecord>> ListActiveTrialsExpiringBetweenAsync(
        DateTimeOffset from, DateTimeOffset to, string notifiedFlag, CancellationToken ct = default)
    {
        Func<LicenseRecord, bool> flagFalse = notifiedFlag switch
        {
            "t3" => r => !r.TrialNotifiedT3,
            "t1" => r => !r.TrialNotifiedT1,
            "expired" => r => !r.TrialNotifiedExpired,
            _ => throw new ArgumentOutOfRangeException(nameof(notifiedFlag)),
        };
        var hits = ByEmail.Values
            .Where(r => r.Tier == BoltMate.Licensing.Contracts.LicenseTier.Trial
                        && r.Status == "active"
                        && r.ExpiresAt is { } e && e >= from && e < to
                        && flagFalse(r))
            .ToList();
        return Task.FromResult<IReadOnlyList<LicenseRecord>>(hits);
    }
}

internal sealed class FakeEmailNotifier : IEmailNotifier
{
    public List<(string To, string LicenseId)> Purchase { get; } = new();
    public List<(string To, int DaysLeft, DateTimeOffset Expires)> TrialEnding { get; } = new();
    public List<string> Expired { get; } = new();
    public List<(string To, string Subject)> SupportAck { get; } = new();

    public Task PurchaseConfirmationAsync(string toEmail, string licenseId, CancellationToken ct = default)
    {
        Purchase.Add((toEmail, licenseId));
        return Task.CompletedTask;
    }

    public Task TrialEndingAsync(string toEmail, int daysLeft, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        TrialEnding.Add((toEmail, daysLeft, expiresAt));
        return Task.CompletedTask;
    }

    public Task TrialExpiredAsync(string toEmail, CancellationToken ct = default)
    {
        Expired.Add(toEmail);
        return Task.CompletedTask;
    }

    public Task SupportTicketAckAsync(string toEmail, string subject, CancellationToken ct = default)
    {
        SupportAck.Add((toEmail, subject));
        return Task.CompletedTask;
    }
}

internal sealed class FakeIdTokenValidator : IIdTokenValidator
{
    public Func<string, ValidatedIdToken?>? OnValidate { get; set; }
    public Task<ValidatedIdToken?> ValidateAsync(string idToken, CancellationToken ct = default)
        => Task.FromResult(OnValidate?.Invoke(idToken));
}

internal sealed class FakeJwtSigner : IJwtSigner
{
    public List<LicenseClaims> Signed { get; } = new();
    public Task<string> SignAsync(LicenseClaims claims, CancellationToken ct = default)
    {
        Signed.Add(claims);
        return Task.FromResult($"jwt-{claims.LicenseId}-{claims.ExpiresAt.ToUnixTimeSeconds()}");
    }
}

internal sealed class FakeRateLimiter : IRateLimiter
{
    public bool Allow { get; set; } = true;
    public string? DenyReason { get; set; }
    public int? RetryAfterSeconds { get; set; }
    public Task<RateLimitDecision> CheckAsync(string licenseId, CancellationToken ct = default)
        => Task.FromResult(Allow
            ? new RateLimitDecision(true, null, null)
            : new RateLimitDecision(false, RetryAfterSeconds, DenyReason ?? "rate_limited"));
}

internal sealed class FakeRefreshLogRepository : IRefreshLogRepository
{
    public List<(string LicenseId, string Sub, DateTimeOffset At)> Records { get; } = new();
    public Task RecordAsync(string licenseId, string oauthSubject, DateTimeOffset at, CancellationToken ct = default)
    {
        Records.Add((licenseId, oauthSubject, at));
        return Task.CompletedTask;
    }

    public Task<int> CountSinceAsync(string licenseId, DateTimeOffset since, CancellationToken ct = default)
        => Task.FromResult(Records.Count(r => r.LicenseId == licenseId && r.At >= since));

    public Task<int> DeleteByLicenseIdAsync(string licenseId, CancellationToken ct = default)
    {
        var removed = Records.RemoveAll(r => r.LicenseId == licenseId);
        return Task.FromResult(removed);
    }
}

internal sealed class FakeSupportBundleStore : ISupportBundleStore
{
    public List<(string FileName, string ContentType, long Bytes)> Uploads { get; } = new();
    public Uri NextUrl { get; set; } = new Uri("https://example.test/bundle?sas=stub");

    public async Task<SupportBundleUpload> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct).ConfigureAwait(false);
        Uploads.Add((fileName, contentType, ms.Length));
        return new SupportBundleUpload($"blob/{fileName}", NextUrl, ms.Length);
    }
}

internal sealed class FakeSupportTicketSink : ISupportTicketSink
{
    public List<SupportTicket> Tickets { get; } = new();
    public Task SubmitAsync(SupportTicket ticket, CancellationToken ct = default)
    {
        Tickets.Add(ticket);
        return Task.CompletedTask;
    }
}
