using System.Net;
using System.Text;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class DevLocalMembershipRouteCompositionTests
{
    private const string Pin =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly Uri CatalogUrl = new(
        "http://192.168.50.10:18120/api/network/membership-route-catalog");

    [Fact]
    public void Resolve_PairEnablesOnlyExplicitNonProductionPhysicalDevelopment()
    {
        var configuration = DevLocalMembershipRouteConfiguration.Resolve(
            CatalogUrl.AbsoluteUri,
            Pin,
            explicitDevelopmentProfile: true,
            productionBuild: false);

        Assert.NotNull(configuration);
        Assert.Equal(CatalogUrl, configuration.CatalogUrl);
        Assert.Equal(Pin, configuration.ExpectedArtifactSha256);
    }

    [Fact]
    public void Resolve_MissingPairLeavesNormalRoutingDormant()
    {
        Assert.Null(DevLocalMembershipRouteConfiguration.Resolve(
            null,
            null,
            explicitDevelopmentProfile: false,
            productionBuild: true));
    }

    [Theory]
    [InlineData("http://192.168.50.10:18120/api/network/membership-route-catalog", null)]
    [InlineData(null, Pin)]
    public void Resolve_OneSettingFailsStartupClosed(string? url, string? pin)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            DevLocalMembershipRouteConfiguration.Resolve(
                url,
                pin,
                explicitDevelopmentProfile: true,
                productionBuild: false));

        Assert.DoesNotContain(url ?? Pin, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://membership.example/api/network/membership-route-catalog")]
    [InlineData("http://8.8.8.8/api/network/membership-route-catalog")]
    [InlineData("http://localhost/api/network/membership-route-catalog")]
    [InlineData("http://192.168.50.10:18120/")]
    [InlineData("http://user@192.168.50.10:18120/api/network/membership-route-catalog")]
    [InlineData("http://192.168.50.10:18120/api/network/membership-route-catalog?pin=secret")]
    public void Resolve_RejectsRemoteTofuAndNonExactCatalogUrls(string url)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            DevLocalMembershipRouteConfiguration.Resolve(
                url,
                Pin,
                explicitDevelopmentProfile: true,
                productionBuild: false));

        Assert.DoesNotContain(url, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Pin, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_RejectsProductionAndImplicitDebugActivation()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DevLocalMembershipRouteConfiguration.Resolve(
                CatalogUrl.AbsoluteUri,
                Pin,
                explicitDevelopmentProfile: true,
                productionBuild: true));
        Assert.Throws<InvalidOperationException>(() =>
            DevLocalMembershipRouteConfiguration.Resolve(
                CatalogUrl.AbsoluteUri,
                Pin,
                explicitDevelopmentProfile: false,
                productionBuild: false));
    }

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("aaaa")]
    public void Resolve_RejectsNonCanonicalPinWithoutEcho(string pin)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            DevLocalMembershipRouteConfiguration.Resolve(
                CatalogUrl.AbsoluteUri,
                pin,
                explicitDevelopmentProfile: true,
                productionBuild: false));

        Assert.DoesNotContain(pin, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredProvider_PreBindAndDoubleBindFailClosed()
    {
        using var provider = Provider(new StaticHttpHandler("{}"));

        Assert.Throws<MembershipRouteCatalogException>(() =>
        {
            _ = provider.GetCatalogAsync();
        });
        provider.Bind(new InMemorySessionStore());
        Assert.True(provider.IsBound);
        Assert.Throws<InvalidOperationException>(() =>
            provider.Bind(new InMemorySessionStore()));
    }

    [Fact]
    public async Task DeferredProvider_ConcurrentBindHasExactlyOneWinner()
    {
        using var provider = Provider(new StaticHttpHandler("{}"));
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                try
                {
                    provider.Bind(new InMemorySessionStore());
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }))
            .ToArray();

        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, static won => won);
        Assert.True(provider.IsBound);
    }

    [Fact]
    public async Task DeferredProvider_PinMismatchFailsDispatchWithSafeDiagnostics()
    {
        var handler = new StaticHttpHandler("""{"unexpected":"artifact"}""");
        using var provider = Provider(handler);
        provider.Bind(new InMemorySessionStore());

        var error = await Assert.ThrowsAsync<MembershipRouteCatalogException>(
            () => provider.GetCatalogAsync());

        Assert.Equal("Membership artifact SHA-256 pin does not match.", error.Message);
        Assert.DoesNotContain(CatalogUrl.AbsoluteUri, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Pin, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }

    private static DeferredVerifiedMembershipRouteCatalogProvider Provider(
        HttpMessageHandler handler) =>
        new(
            new DevLocalMembershipRouteConfiguration(CatalogUrl, Pin),
            new HttpClient(handler),
            new InMemoryMembershipRouteArtifactCache());

    internal static DeferredVerifiedMembershipRouteCatalogProvider
        CreateProviderForCompositionTest() =>
        Provider(new StaticHttpHandler("{}"));

    private sealed class StaticHttpHandler(string artifact) : HttpMessageHandler
    {
        private int requestCount;

        public int RequestCount => Volatile.Read(ref requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(artifact))
            });
        }
    }
}
