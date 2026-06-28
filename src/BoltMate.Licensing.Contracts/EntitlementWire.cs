using System.Text.Json.Serialization;

namespace BoltMate.Licensing.Contracts;

/// <summary>
/// POSTed by the desktop app on every entitlement request. All fields
/// optional — the only required signal is the Bearer ID token in the
/// Authorization header. <see cref="HardwareIdHash"/> is used by the
/// backend's anti-trial-reuse check; clients SHOULD provide it on
/// first-time provisioning, but a missing hash just means we can't
/// enforce the 12-month re-trial block for that session.
/// </summary>
public sealed record EntitlementRequest(
    [property: JsonPropertyName("hardware_id_hash")] string? HardwareIdHash);

public sealed record EntitlementResponse(
    [property: JsonPropertyName("jwt")] string Jwt,
    [property: JsonPropertyName("next_refresh_after")] long? NextRefreshAfterUnix);

public sealed record EntitlementErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("retry_after_seconds")] int? RetryAfterSeconds);

public static class EntitlementErrorCodes
{
    public const string LicenseNotFound = "license_not_found";
    public const string LicenseRevoked = "license_revoked";
    public const string RateLimited = "rate_limited";
    public const string InvalidIdToken = "invalid_id_token";
    public const string MalformedRequest = "malformed_request";

    /// <summary>This hardware already used its trial within the
    /// reuse-block window. Buy the lifetime license to continue.</summary>
    public const string TrialReused = "trial_reused";
}
