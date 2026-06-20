using System;
using System.Collections.Generic;

namespace BoltMate.Licensing.Configuration;

public sealed class LicensingOptions
{
    public string ServiceName { get; set; } = "BoltMate";

    public string SecureStoreKey { get; set; } = "license.jwt";

    public string Issuer { get; set; } = "https://license.boltmate.dev";

    public string PublicKeyPem { get; set; } = string.Empty;

    public Uri EntitlementEndpoint { get; set; } = new("https://license.boltmate.dev/api/entitlement");

    public Uri AuthorizeEndpoint { get; set; } = new("https://login.boltmate.dev/oauth2/authorize");

    public Uri TokenEndpoint { get; set; } = new("https://login.boltmate.dev/oauth2/token");

    public string OAuthClientId { get; set; } = string.Empty;

    public IList<string> OAuthScopes { get; set; } = new List<string> { "openid", "email", "profile" };

    public TimeSpan RefreshBeforeExpiry { get; set; } = TimeSpan.FromDays(3);

    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
