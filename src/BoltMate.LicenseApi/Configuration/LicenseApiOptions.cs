namespace BoltMate.LicenseApi.Configuration;

public sealed class LicenseApiOptions
{
    public const string SectionName = "LicenseApi";

    public string Issuer { get; set; } = string.Empty;

    public string KeyVaultUri { get; set; } = string.Empty;
    public string SigningKeyName { get; set; } = "license-signing-key";

    public string CosmosEndpoint { get; set; } = string.Empty;
    public string CosmosDatabase { get; set; } = "boltmate";
    public string LicensesContainer { get; set; } = "Licenses";
    public string RefreshLogContainer { get; set; } = "RefreshLog";

    public string EntraTenantId { get; set; } = string.Empty;
    public string EntraClientId { get; set; } = string.Empty;
    public string EntraAuthorityHost { get; set; } = "https://login.microsoftonline.com";

    public string StripeWebhookSecret { get; set; } = string.Empty;
    public string StripeSecretKey { get; set; } = string.Empty;

    public string SupportEmailTo { get; set; } = string.Empty;
    public string ResendApiKey { get; set; } = string.Empty;

    /// <summary>e.g. https://boltmateprodstorage.blob.core.windows.net</summary>
    public string StorageAccountUri { get; set; } = string.Empty;

    public string SupportBundlesContainer { get; set; } = "support-bundles";

    /// <summary>How long the SAS URL we email to ourselves stays valid.
    /// Bundle is auto-deleted by storage lifecycle policy after 30 days
    /// regardless — this caps the URL's exposure window.</summary>
    public int SupportBundleSasLifetimeDays { get; set; } = 30;

    /// <summary>Hard cap on incoming multipart upload size in MB to guard
    /// against accidental log floods or malicious payloads.</summary>
    public int SupportBundleMaxSizeMB { get; set; } = 25;

    public int RefreshTokenLifetimeDays { get; set; } = 30;
    public int Refresh24hCap { get; set; } = 5;
    public int Refresh30dCap { get; set; } = 20;

    /// <summary>How many days a new Trial license stays valid from issue.</summary>
    public int TrialLengthDays { get; set; } = 14;

    /// <summary>How many days after a trial was first issued we refuse to
    /// re-issue another trial to the same hardware. Prevents users from
    /// looping email accounts to get unlimited trials. 365 days = 12 mo.</summary>
    public int TrialReuseBlockDays { get; set; } = 365;
}
