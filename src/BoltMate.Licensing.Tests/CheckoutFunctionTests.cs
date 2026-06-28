using System.IO;
using System.Net;
using System.Threading.Tasks;
using BoltMate.LicenseApi.Configuration;
using BoltMate.LicenseApi.Functions;
using BoltMate.LicenseApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BoltMate.Licensing.Tests;

public sealed class CheckoutFunctionTests
{
    // Stripe.PriceService + SessionService talk to the live API at
    // request time, so the only thing we can sanity-test offline is
    // the auth gate. Full happy-path round-trips run as integration
    // tests once the Function App is deployed against the sandbox.

    [Fact]
    public async Task Missing_bearer_returns_401()
    {
        var fn = Build();
        var req = MakePost(bearer: null);

        var result = await fn.Run(req, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal((int)HttpStatusCode.Unauthorized, obj.StatusCode);
    }

    [Fact]
    public async Task Invalid_bearer_returns_401()
    {
        var fakes = new FakeIdTokenValidator { OnValidate = _ => null };
        var fn = Build(fakes);
        var req = MakePost(bearer: "garbage");

        var result = await fn.Run(req, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal((int)HttpStatusCode.Unauthorized, obj.StatusCode);
    }

    private static CheckoutFunction Build(FakeIdTokenValidator? idTokens = null)
        => new(
            idTokens ?? new FakeIdTokenValidator(),
            Options.Create(new LicenseApiOptions
            {
                StripeSecretKey = "sk_test_dummy",
                StripePriceLookupKey = "boltmate_lifetime",
                SiteOrigin = "https://boltmate.app",
            }),
            NullLogger<CheckoutFunction>.Instance);

    private static HttpRequest MakePost(string? bearer)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Body = new MemoryStream();
        ctx.Request.ContentLength = 0;
        if (bearer is not null) ctx.Request.Headers.Authorization = $"Bearer {bearer}";
        return ctx.Request;
    }
}
