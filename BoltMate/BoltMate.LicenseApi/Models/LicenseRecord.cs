using System;
using BoltMate.Licensing.Contracts;

namespace BoltMate.LicenseApi.Models;

public sealed class LicenseRecord
{
    public string Id { get; set; } = string.Empty;
    public string PartitionKey { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? OAuthSubject { get; set; }
    public string Sku { get; set; } = string.Empty;
    public LicenseTier Tier { get; set; } = LicenseTier.Pro;
    public string Status { get; set; } = "active";
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? StripeCustomerId { get; set; }
    public string? StripeCheckoutSessionId { get; set; }
    public string? StripePaymentIntentId { get; set; }
}
