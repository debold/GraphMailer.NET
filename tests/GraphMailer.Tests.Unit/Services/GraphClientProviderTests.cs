using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// Client caching in <see cref="GraphClientProvider"/>: the cached GraphServiceClient
/// must be rebuilt when the CREDENTIAL changes (secret rotation via config reload),
/// not only when tenant/client id/auth method change — otherwise the stale credential
/// keeps failing until a service restart.
/// </summary>
public sealed class GraphClientProviderTests
{
    private static GraphApiOptions Opts(string secret) => new()
    {
        TenantId = "tenant-id",
        ClientId = "client-id",
        ClientSecret = secret,
    };

    [Fact]
    public void GetClient_SameOptions_ReturnsCachedInstance()
    {
        var sut = new GraphClientProvider(NullLogger<GraphClientProvider>.Instance);

        var first = sut.GetClient(Opts("secret-A"));
        var second = sut.GetClient(Opts("secret-A"));

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void GetClient_RotatedClientSecret_RebuildsClient()
    {
        // Regression: the cache key ignored the secret value, so a rotated secret kept
        // the stale credential until restart — a delivery outage a reload should fix.
        var sut = new GraphClientProvider(NullLogger<GraphClientProvider>.Instance);

        var before = sut.GetClient(Opts("secret-A"));
        var after = sut.GetClient(Opts("secret-B"));

        after.Should().NotBeSameAs(before);
    }
}
