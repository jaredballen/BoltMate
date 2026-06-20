using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Security.KeyVault.Keys.Cryptography;
using BoltMate.Licensing.Contracts;

namespace BoltMate.LicenseApi.Services;

internal sealed class KeyVaultJwtSigner : IJwtSigner
{
    private readonly CryptographyClient _crypto;

    public KeyVaultJwtSigner(CryptographyClient crypto) => _crypto = crypto;

    public async Task<string> SignAsync(LicenseClaims claims, CancellationToken ct = default)
    {
        var header = new { alg = "RS256", typ = "JWT" };
        var payload = new
        {
            sub = claims.Subject,
            email = claims.Email,
            lic = claims.LicenseId,
            sku = claims.Sku,
            tier = claims.Tier.ToString(),
            iss = claims.Issuer,
            iat = claims.IssuedAt.ToUnixTimeSeconds(),
            exp = claims.ExpiresAt.ToUnixTimeSeconds()
        };

        var headerB64 = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header));
        var payloadB64 = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signing = Encoding.UTF8.GetBytes($"{headerB64}.{payloadB64}");

        var result = await _crypto.SignDataAsync(SignatureAlgorithm.RS256, signing, ct).ConfigureAwait(false);
        return $"{headerB64}.{payloadB64}.{Base64Url(result.Signature)}";
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
