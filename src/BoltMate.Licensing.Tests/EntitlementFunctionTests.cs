using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BoltMate.LicenseApi.Configuration;
using BoltMate.LicenseApi.Functions;
using BoltMate.LicenseApi.Models;
using BoltMate.LicenseApi.Services;
using BoltMate.Licensing.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BoltMate.Licensing.Tests;

public sealed class EntitlementFunctionTests
{
    [Fact]
    public async Task First_call_with_no_license_provisions_trial()
    {
        var (fn, fakes) = Build();
        var req = MakePostRequest(bearer: "valid", body: new { hardware_id_hash = "hw-1" });

        var result = await fn.Run(req, default);

        Assert.IsType<OkObjectResult>(result);
        var record = fakes.Licenses.ByEmail["jared@example.com"];
        Assert.Equal(LicenseTier.Trial, record.Tier);
        Assert.Equal(LicenseSkus.Boltmate, record.Sku);
        Assert.Equal("hw-1", record.HardwareIdHash);
        Assert.NotNull(record.TrialOriginAt);
        Assert.NotNull(record.ExpiresAt);
        Assert.Single(fakes.Signer.Signed);
    }

    [Fact]
    public async Task Trial_reused_on_same_hardware_within_window_is_blocked()
    {
        var (fn, fakes) = Build();
        fakes.Licenses.BlockHardwareReuse = true;
        var req = MakePostRequest(bearer: "valid", body: new { hardware_id_hash = "hw-stolen" });

        var result = await fn.Run(req, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal((int)HttpStatusCode.Forbidden, obj.StatusCode);
        var error = Assert.IsType<EntitlementErrorResponse>(obj.Value);
        Assert.Equal(EntitlementErrorCodes.TrialReused, error.Error);
        Assert.Empty(fakes.Signer.Signed);
        Assert.Empty(fakes.Licenses.ByEmail);
    }

    [Fact]
    public async Task Trial_block_skipped_when_hardware_hash_not_supplied()
    {
        var (fn, fakes) = Build();
        fakes.Licenses.BlockHardwareReuse = true; // would block IF asked
        var req = MakePostRequest(bearer: "valid", body: new { });

        var result = await fn.Run(req, default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(fakes.Licenses.HardwareReuseChecks);
        Assert.Single(fakes.Signer.Signed);
    }

    [Fact]
    public async Task Existing_active_license_issues_entitlement()
    {
        var (fn, fakes) = Build();
        fakes.Licenses.ByEmail["jared@example.com"] = new LicenseRecord
        {
            Id = "lic_existing",
            Email = "jared@example.com",
            Sku = LicenseSkus.Boltmate,
            Tier = LicenseTier.Boltmate,
            Status = "active",
            IssuedAt = System.DateTimeOffset.UtcNow.AddDays(-30),
        };
        var req = MakePostRequest(bearer: "valid");

        var result = await fn.Run(req, default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(LicenseTier.Boltmate, fakes.Signer.Signed[0].Tier);
        Assert.Equal("lic_existing", fakes.Signer.Signed[0].LicenseId);
    }

    [Fact]
    public async Task Revoked_license_returns_403()
    {
        var (fn, fakes) = Build();
        fakes.Licenses.ByEmail["jared@example.com"] = new LicenseRecord
        {
            Id = "lic_rev",
            Email = "jared@example.com",
            Sku = LicenseSkus.Boltmate,
            Tier = LicenseTier.Boltmate,
            Status = "revoked",
        };
        var req = MakePostRequest(bearer: "valid");

        var result = await fn.Run(req, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal((int)HttpStatusCode.Forbidden, obj.StatusCode);
    }

    [Fact]
    public async Task Trial_jwt_expiry_clamps_to_license_expiresat()
    {
        var (fn, fakes) = Build();
        var trialExpiry = System.DateTimeOffset.UtcNow.AddDays(2); // 2d left
        fakes.Licenses.ByEmail["jared@example.com"] = new LicenseRecord
        {
            Id = "lic_trial",
            Email = "jared@example.com",
            Sku = LicenseSkus.Boltmate,
            Tier = LicenseTier.Trial,
            Status = "active",
            ExpiresAt = trialExpiry,
        };
        var req = MakePostRequest(bearer: "valid");

        await fn.Run(req, default);

        var claims = fakes.Signer.Signed[0];
        // Expiry can't be later than license's own ExpiresAt — refresh token
        // lifetime is 30 days by default but trial only has 2 left.
        Assert.True(claims.ExpiresAt <= trialExpiry);
    }

    [Fact]
    public async Task Missing_bearer_returns_401()
    {
        var (fn, _) = Build();
        var req = MakePostRequest(bearer: null);

        var result = await fn.Run(req, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal((int)HttpStatusCode.Unauthorized, obj.StatusCode);
    }

    private static (EntitlementFunction Function, Fakes Fakes) Build()
    {
        var fakes = new Fakes();
        fakes.IdTokens.OnValidate = _ => new ValidatedIdToken("oauth-sub-1", "jared@example.com", "Jared");
        var fn = new EntitlementFunction(
            fakes.IdTokens, fakes.Licenses, fakes.RefreshLog, fakes.RateLimiter, fakes.Signer,
            Options.Create(new LicenseApiOptions { Issuer = "https://test", TrialLengthDays = 14, RefreshTokenLifetimeDays = 30, TrialReuseBlockDays = 365 }),
            NullLogger<EntitlementFunction>.Instance);
        return (fn, fakes);
    }

    private static HttpRequest MakePostRequest(string? bearer, object? body = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        if (bearer is not null) ctx.Request.Headers.Authorization = $"Bearer {bearer}";
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentLength = bytes.Length;
            ctx.Request.ContentType = "application/json";
        }
        return ctx.Request;
    }

    internal sealed class Fakes
    {
        public FakeIdTokenValidator IdTokens { get; } = new();
        public FakeLicenseRepository Licenses { get; } = new();
        public FakeRefreshLogRepository RefreshLog { get; } = new();
        public FakeRateLimiter RateLimiter { get; } = new();
        public FakeJwtSigner Signer { get; } = new();
    }
}
