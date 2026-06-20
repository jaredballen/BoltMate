using System;

namespace LogiPlusSwitcher.LicenseApi.Models;

public sealed class RefreshRecord
{
    public string Id { get; set; } = string.Empty;
    public string PartitionKey { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
    public string OAuthSubject { get; set; } = string.Empty;
    public string? IpHash { get; set; }
    public int Ttl { get; set; } = 60 * 60 * 24 * 31;
}
