using System.Text.Json.Serialization;

namespace LogiPlusSwitcher.Licensing.Contracts;

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
}
