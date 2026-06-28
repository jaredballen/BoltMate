using System.Threading;
using System.Threading.Tasks;

namespace BoltMate.LicenseApi.Services;

/// <summary>
/// Posts a GitHub <c>repository_dispatch</c> event that kicks off a
/// workflow on the BoltMate site repo. Used by
/// <see cref="StripeWebhookHandler"/> to re-trigger the SWA static
/// build whenever a Stripe Price object changes, so the published
/// site picks up the new amount without a manual deploy.
/// </summary>
public interface IGitHubDispatcher
{
    Task DispatchAsync(string eventType, object? clientPayload, CancellationToken ct = default);
}
