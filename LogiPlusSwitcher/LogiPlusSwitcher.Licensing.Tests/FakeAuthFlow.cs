using System.Threading;
using System.Threading.Tasks;
using LogiPlusSwitcher.Licensing.Activation;

namespace LogiPlusSwitcher.Licensing.Tests;

internal sealed class FakeAuthFlow : IBrowserAuthFlow
{
    public string IdToken { get; set; } = "fake-id-token";

    public int CallCount { get; private set; }

    public Task<AuthFlowResult> AuthenticateAsync(CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(new AuthFlowResult(IdToken, null));
    }
}
