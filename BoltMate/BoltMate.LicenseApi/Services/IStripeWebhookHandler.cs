using System.Threading;
using System.Threading.Tasks;

namespace BoltMate.LicenseApi.Services;

public interface IStripeWebhookHandler
{
    Task HandleAsync(string payload, string signature, CancellationToken ct = default);
}
