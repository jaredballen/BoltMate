using System.Threading;
using System.Threading.Tasks;

namespace BoltMate.LicenseApi.Services;

public interface ISupportTicketSink
{
    Task SubmitAsync(SupportTicket ticket, CancellationToken ct = default);
}

public sealed record SupportTicket(
    string FromEmail,
    string? FromName,
    string Subject,
    string Body,
    string? LicenseId);
