using System;
using System.Collections.Generic;
using BoltMate.Licensing.Activation;
using BoltMate.Licensing.Configuration;
using BoltMate.Licensing.Crypto;
using BoltMate.Licensing.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoltMate.Licensing.DependencyInjection;

public static class ServiceCollectionExtensions
{
    private const string EntitlementHttpClient = "BoltMate.Licensing.Entitlement";
    private const string AuthFlowHttpClient = "BoltMate.Licensing.Auth";

    public static IServiceCollection AddBoltMateLicensing(
        this IServiceCollection services,
        Action<LicensingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.TryAddSingleton<IClock, SystemClock>();

        services.AddHttpClient(EntitlementHttpClient, (sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<LicensingOptions>>().Value;
            http.Timeout = opts.HttpTimeout;
        });
        services.AddHttpClient(AuthFlowHttpClient, (sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<LicensingOptions>>().Value;
            http.Timeout = opts.HttpTimeout;
        });

        services.TryAddSingleton<ISecureStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LicensingOptions>>().Value;
            return SecureStoreFactory.CreateForCurrentPlatform(opts.ServiceName);
        });

        services.TryAddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LicensingOptions>>().Value;
            return new JwtVerifier(opts.PublicKeyPem, opts.Issuer);
        });

        services.TryAddSingleton<IEntitlementClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LicensingOptions>>().Value;
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new EntitlementClient(factory.CreateClient(EntitlementHttpClient), opts.EntitlementEndpoint);
        });

        services.TryAddSingleton<IBrowserAuthFlow>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LicensingOptions>>().Value;
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var authOptions = new LoopbackAuthOptions(
                opts.AuthorizeEndpoint,
                opts.TokenEndpoint,
                opts.OAuthClientId,
                new List<string>(opts.OAuthScopes));
            return new LoopbackAuthFlow(factory.CreateClient(AuthFlowHttpClient), authOptions);
        });

        services.TryAddSingleton<LicenseGate>(sp => new LicenseGate(
            sp.GetRequiredService<ISecureStore>(),
            sp.GetRequiredService<JwtVerifier>(),
            sp.GetRequiredService<IBrowserAuthFlow>(),
            sp.GetRequiredService<IEntitlementClient>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IOptions<LicensingOptions>>().Value,
            sp.GetService<ILogger<LicenseGate>>()));
        services.TryAddSingleton<ILicenseGate>(sp => sp.GetRequiredService<LicenseGate>());
        services.TryAddSingleton<IPeerCryptoKeySource>(sp => sp.GetRequiredService<LicenseGate>());

        return services;
    }
}
