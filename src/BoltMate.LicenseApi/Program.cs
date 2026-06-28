using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using Azure.Storage.Blobs;
using BoltMate.LicenseApi.Configuration;
using BoltMate.LicenseApi.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddOptions<LicenseApiOptions>()
    .Configure<IConfiguration>((opts, cfg) => cfg.GetSection(LicenseApiOptions.SectionName).Bind(opts));

builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());

builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<LicenseApiOptions>>().Value;
    var cred = sp.GetRequiredService<TokenCredential>();
    return new CosmosClient(opts.CosmosEndpoint, cred, new CosmosClientOptions
    {
        SerializerOptions = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
        }
    });
});

builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<LicenseApiOptions>>().Value;
    var cred = sp.GetRequiredService<TokenCredential>();
    var keyClient = new KeyClient(new Uri(opts.KeyVaultUri), cred);
    var key = keyClient.GetKey(opts.SigningKeyName).Value;
    return new CryptographyClient(key.Id, cred);
});

builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<LicenseApiOptions>>().Value;
    var cred = sp.GetRequiredService<TokenCredential>();
    return new BlobServiceClient(new Uri(opts.StorageAccountUri), cred);
});

builder.Services.AddSingleton<IJwtSigner, KeyVaultJwtSigner>();
builder.Services.AddSingleton<IIdTokenValidator, EntraIdTokenValidator>();
builder.Services.AddSingleton<ILicenseRepository, CosmosLicenseRepository>();
builder.Services.AddSingleton<IRefreshLogRepository, CosmosRefreshLogRepository>();
builder.Services.AddSingleton<IRateLimiter, RateLimiter>();
builder.Services.AddSingleton<IStripeWebhookHandler, StripeWebhookHandler>();
builder.Services.AddSingleton<ISupportBundleStore, BlobSupportBundleStore>();
builder.Services.AddSingleton<ISupportTicketSink, ResendSupportTicketSink>();
builder.Services.AddSingleton<IEmailNotifier, ResendEmailNotifier>();
builder.Services.AddSingleton<IGitHubDispatcher, GitHubDispatcher>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Build().Run();
