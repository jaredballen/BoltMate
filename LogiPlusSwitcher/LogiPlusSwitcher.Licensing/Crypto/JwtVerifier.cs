using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogiPlusSwitcher.Licensing.Contracts;

namespace LogiPlusSwitcher.Licensing.Crypto;

public sealed class JwtVerifier : IDisposable
{
    private readonly RSA _publicKey;
    private readonly string _expectedIssuer;

    public JwtVerifier(string publicKeyPem, string expectedIssuer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedIssuer);

        _publicKey = RSA.Create();
        _publicKey.ImportFromPem(publicKeyPem);
        _expectedIssuer = expectedIssuer;
    }

    public JwtVerificationResult Verify(string jwt, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(jwt))
            return JwtVerificationResult.Fail("empty token");

        var parts = jwt.Split('.');
        if (parts.Length != 3)
            return JwtVerificationResult.Fail("malformed token");

        byte[] headerBytes, payloadBytes, sigBytes;
        try
        {
            headerBytes = Base64Url.Decode(parts[0]);
            payloadBytes = Base64Url.Decode(parts[1]);
            sigBytes = Base64Url.Decode(parts[2]);
        }
        catch (FormatException)
        {
            return JwtVerificationResult.Fail("base64url decode failed");
        }

        using (var headerDoc = JsonDocument.Parse(headerBytes))
        {
            if (!headerDoc.RootElement.TryGetProperty("alg", out var alg) ||
                alg.GetString() != "RS256")
                return JwtVerificationResult.Fail("unsupported alg");
        }

        var signed = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
        if (!_publicKey.VerifyData(signed, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            return JwtVerificationResult.Fail("signature invalid");

        LicenseClaims claims;
        try
        {
            claims = ParsePayload(payloadBytes);
        }
        catch (Exception ex)
        {
            return JwtVerificationResult.Fail($"payload parse failed: {ex.Message}");
        }

        if (!string.Equals(claims.Issuer, _expectedIssuer, StringComparison.Ordinal))
            return JwtVerificationResult.Fail("issuer mismatch");

        if (claims.ExpiresAt <= now)
            return JwtVerificationResult.Expired(claims);

        return JwtVerificationResult.Ok(claims);
    }

    private static LicenseClaims ParsePayload(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var sub = root.GetProperty("sub").GetString() ?? throw new FormatException("sub missing");
        var email = root.GetProperty("email").GetString() ?? throw new FormatException("email missing");
        var licenseId = root.GetProperty("lic").GetString() ?? throw new FormatException("lic missing");
        var sku = root.GetProperty("sku").GetString() ?? throw new FormatException("sku missing");
        var tierStr = root.GetProperty("tier").GetString() ?? "Free";
        var iss = root.GetProperty("iss").GetString() ?? throw new FormatException("iss missing");
        var iat = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("iat").GetInt64());
        var exp = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("exp").GetInt64());

        var tier = Enum.TryParse<LicenseTier>(tierStr, ignoreCase: true, out var parsed) ? parsed : LicenseTier.Free;

        return new LicenseClaims(sub, email, licenseId, sku, tier, iss, iat, exp);
    }

    public void Dispose() => _publicKey.Dispose();
}

public sealed record JwtVerificationResult(
    bool IsValid,
    bool IsExpired,
    LicenseClaims? Claims,
    string? Reason)
{
    public static JwtVerificationResult Ok(LicenseClaims claims) => new(true, false, claims, null);
    public static JwtVerificationResult Expired(LicenseClaims claims) => new(false, true, claims, "expired");
    public static JwtVerificationResult Fail(string reason) => new(false, false, null, reason);
}

internal static class Base64Url
{
    public static byte[] Decode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        var pad = padded.Length % 4;
        if (pad == 2) padded += "==";
        else if (pad == 3) padded += "=";
        else if (pad != 0) throw new FormatException("invalid base64url length");
        return Convert.FromBase64String(padded);
    }
}
