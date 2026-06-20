using System;
using LogiPlusSwitcher.Licensing;
using LogiPlusSwitcher.Licensing.Contracts;
using LogiPlusSwitcher.Licensing.Crypto;

namespace LogiPlusSwitcher.Licensing.Tests;

public sealed class JwtVerifierTests
{
    private const string Issuer = "https://test-issuer.example.com";

    [Fact]
    public void Valid_token_round_trips_claims()
    {
        using var keys = new TestKeys();
        using var verifier = new JwtVerifier(keys.PublicKeyPem, Issuer);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var jwt = MintJwt(keys, now, now.AddDays(30));

        var result = verifier.Verify(jwt, now);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Claims);
        Assert.Equal("user@example.com", result.Claims!.Email);
        Assert.Equal("lic-123", result.Claims.LicenseId);
        Assert.Equal(LicenseTier.Pro, result.Claims.Tier);
    }

    [Fact]
    public void Expired_token_returns_IsExpired()
    {
        using var keys = new TestKeys();
        using var verifier = new JwtVerifier(keys.PublicKeyPem, Issuer);
        var iat = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var exp = iat.AddDays(30);
        var now = exp.AddDays(1);

        var jwt = MintJwt(keys, iat, exp);

        var result = verifier.Verify(jwt, now);

        Assert.False(result.IsValid);
        Assert.True(result.IsExpired);
        Assert.NotNull(result.Claims);
    }

    [Fact]
    public void Wrong_issuer_fails()
    {
        using var keys = new TestKeys();
        using var verifier = new JwtVerifier(keys.PublicKeyPem, "https://other-issuer.example.com");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var jwt = MintJwt(keys, now, now.AddDays(30));

        var result = verifier.Verify(jwt, now);

        Assert.False(result.IsValid);
        Assert.False(result.IsExpired);
        Assert.Equal("issuer mismatch", result.Reason);
    }

    [Fact]
    public void Tampered_signature_fails()
    {
        using var keys = new TestKeys();
        using var verifier = new JwtVerifier(keys.PublicKeyPem, Issuer);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var jwt = MintJwt(keys, now, now.AddDays(30));
        var parts = jwt.Split('.');
        var tampered = $"{parts[0]}.{parts[1]}.{new string('A', parts[2].Length)}";

        var result = verifier.Verify(tampered, now);

        Assert.False(result.IsValid);
        Assert.Equal("signature invalid", result.Reason);
    }

    [Fact]
    public void Malformed_token_fails()
    {
        using var keys = new TestKeys();
        using var verifier = new JwtVerifier(keys.PublicKeyPem, Issuer);

        var result = verifier.Verify("not-a-jwt", DateTimeOffset.UtcNow);

        Assert.False(result.IsValid);
        Assert.Equal("malformed token", result.Reason);
    }

    internal static string MintJwt(TestKeys keys, DateTimeOffset iat, DateTimeOffset exp)
    {
        return keys.SignJwt(
            new { alg = "RS256", typ = "JWT" },
            new
            {
                sub = "oauth-sub-123",
                email = "user@example.com",
                lic = "lic-123",
                sku = "logiplus-pro",
                tier = "Pro",
                iss = Issuer,
                iat = iat.ToUnixTimeSeconds(),
                exp = exp.ToUnixTimeSeconds()
            });
    }
}
