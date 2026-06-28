using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using BoltMate.LicenseApi.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoltMate.LicenseApi.Services;

/// <summary>
/// Uploads support log bundles to the Storage account's
/// <c>support-bundles</c> container and returns a user-delegation
/// SAS download URL. Uses the Function App's managed identity — no
/// account keys.
/// </summary>
/// <remarks>
/// User-delegation SAS requires the principal to hold a Storage Blob
/// data-plane role (<c>Storage Blob Data Contributor</c> is sufficient).
/// This is granted out-of-band during Phase 0 provisioning.
/// </remarks>
internal sealed class BlobSupportBundleStore : ISupportBundleStore
{
    private readonly BlobServiceClient _serviceClient;
    private readonly LicenseApiOptions _options;
    private readonly ILogger<BlobSupportBundleStore> _log;

    public BlobSupportBundleStore(
        BlobServiceClient serviceClient,
        IOptions<LicenseApiOptions> options,
        ILogger<BlobSupportBundleStore> log)
    {
        _serviceClient = serviceClient;
        _options = options.Value;
        _log = log;
    }

    public async Task<SupportBundleUpload> UploadAsync(
        Stream content,
        string fileName,
        string contentType,
        CancellationToken ct = default)
    {
        var container = _serviceClient.GetBlobContainerClient(_options.SupportBundlesContainer);

        // Use UTC date prefix to keep listings tidy and aligned with the
        // 30-day lifecycle rule. GUID suffix avoids collisions when two
        // bundles arrive in the same second.
        var blobName = $"{DateTimeOffset.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}-{SafeName(fileName)}";
        var blobClient = container.GetBlobClient(blobName);

        await blobClient.UploadAsync(
            content,
            new BlobHttpHeaders { ContentType = contentType },
            cancellationToken: ct).ConfigureAwait(false);

        var props = await blobClient.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);

        // User-delegation SAS — bound to the managed identity's data-
        // plane access. Stored separately from account keys (which we
        // don't have).
        var now = DateTimeOffset.UtcNow;
        var sasLifetime = TimeSpan.FromDays(_options.SupportBundleSasLifetimeDays);
        var udKey = await _serviceClient
            .GetUserDelegationKeyAsync(now.AddMinutes(-5), now.Add(sasLifetime), ct)
            .ConfigureAwait(false);

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = container.Name,
            BlobName = blobName,
            Resource = "b",
            StartsOn = now.AddMinutes(-5),
            ExpiresOn = now.Add(sasLifetime),
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        var sasToken = sasBuilder
            .ToSasQueryParameters(udKey.Value, _serviceClient.AccountName)
            .ToString();

        var url = new UriBuilder(blobClient.Uri) { Query = sasToken }.Uri;
        _log.LogInformation("Stored support bundle {BlobName} ({Bytes} bytes).", blobName, props.Value.ContentLength);
        return new SupportBundleUpload(blobName, url, props.Value.ContentLength);
    }

    private static string SafeName(string name)
    {
        var clean = new char[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            clean[i] = char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_';
        }
        return new string(clean);
    }
}
