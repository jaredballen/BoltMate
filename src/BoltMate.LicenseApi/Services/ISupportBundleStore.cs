using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BoltMate.LicenseApi.Services;

/// <summary>
/// Stores user-submitted support log bundles in object storage and
/// returns a time-limited download URL. Storage lifecycle policy
/// auto-deletes blobs after 30 days regardless of the SAS lifetime.
/// </summary>
public interface ISupportBundleStore
{
    Task<SupportBundleUpload> UploadAsync(
        Stream content,
        string fileName,
        string contentType,
        CancellationToken ct = default);
}

public sealed record SupportBundleUpload(string BlobName, Uri DownloadUrl, long SizeBytes);
