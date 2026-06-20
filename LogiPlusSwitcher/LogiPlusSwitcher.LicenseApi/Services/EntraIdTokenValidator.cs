using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LogiPlusSwitcher.LicenseApi.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Logging;
using System.IdentityModel.Tokens.Jwt;

namespace LogiPlusSwitcher.LicenseApi.Services;

internal sealed class EntraIdTokenValidator : IIdTokenValidator
{
    private readonly LicenseApiOptions _options;
    private readonly ILogger<EntraIdTokenValidator> _log;
    private readonly Lazy<ConfigurationManager<OpenIdConnectConfiguration>> _config;

    public EntraIdTokenValidator(IOptions<LicenseApiOptions> options, ILogger<EntraIdTokenValidator> log)
    {
        _options = options.Value;
        _log = log;

        _config = new Lazy<ConfigurationManager<OpenIdConnectConfiguration>>(() =>
        {
            var metadataUrl = $"{_options.EntraAuthorityHost}/{_options.EntraTenantId}/v2.0/.well-known/openid-configuration";
            return new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataUrl,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever());
        });
    }

    public async Task<ValidatedIdToken?> ValidateAsync(string idToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;

        try
        {
            var config = await _config.Value.GetConfigurationAsync(ct).ConfigureAwait(false);
            var parameters = new TokenValidationParameters
            {
                ValidateAudience = true,
                ValidAudience = _options.EntraClientId,
                ValidateIssuer = true,
                ValidIssuers = new[] { config.Issuer, $"{_options.EntraAuthorityHost}/{_options.EntraTenantId}/v2.0" },
                ValidateLifetime = true,
                IssuerSigningKeys = config.SigningKeys
            };

            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(idToken, parameters, out var _);

            var sub = principal.FindFirst("sub")?.Value ?? principal.FindFirst("oid")?.Value;
            var email = principal.FindFirst("email")?.Value
                ?? principal.FindFirst("preferred_username")?.Value;
            var name = principal.FindFirst("name")?.Value;

            if (sub is null || email is null) return null;
            return new ValidatedIdToken(sub, email, name);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Entra id_token validation failed.");
            return null;
        }
    }
}
