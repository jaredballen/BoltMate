using System.Threading;
using System.Threading.Tasks;

namespace LogiPlusSwitcher.Licensing.Activation;

public interface IBrowserAuthFlow
{
    Task<AuthFlowResult> AuthenticateAsync(CancellationToken ct = default);
}

public sealed record AuthFlowResult(string IdToken, string? RefreshToken);

public sealed class AuthFlowException : System.Exception
{
    public AuthFlowException(string message) : base(message) { }
    public AuthFlowException(string message, System.Exception inner) : base(message, inner) { }
}
