using System;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.Licensing.Activation;
using BoltMate.Licensing.Contracts;

namespace BoltMate.Licensing.Tests;

internal sealed class FakeEntitlementClient : IEntitlementClient
{
    public Func<string, EntitlementResponse>? OnRequest { get; set; }

    public Exception? ThrowOnRequest { get; set; }

    public int CallCount { get; private set; }

    public Task<EntitlementResponse> RequestEntitlementAsync(string idToken, CancellationToken ct = default)
    {
        CallCount++;
        if (ThrowOnRequest is not null) throw ThrowOnRequest;
        if (OnRequest is null) throw new InvalidOperationException("OnRequest not configured.");
        return Task.FromResult(OnRequest(idToken));
    }
}
