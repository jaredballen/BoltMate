using System;
using System.Threading;
using System.Threading.Tasks;

namespace BoltMate.LicenseApi.Services;

public interface ISupportTicketSink
{
    Task SubmitAsync(SupportTicket ticket, CancellationToken ct = default);
}

/// <summary>
/// Inbound support ticket. <see cref="BundleUrl"/> is set when the
/// caller uploaded a log bundle via the in-app "Send logs" flow;
/// site form submissions don't supply one.
/// </summary>
public sealed record SupportTicket(
    string FromEmail,
    string? FromName,
    string Subject,
    string Body,
    string? LicenseId,
    Uri? BundleUrl,
    long? BundleSizeBytes,
    string? Source);
