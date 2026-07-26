using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using System.Collections.Concurrent;
using System.Net;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class RoutedRuntimeConfigurationTests
{
    private const string RouterOne = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string RouterTwo = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string RouterThree = "3333333333333333333333333333333333333333333333333333333333333333";
    private const string RouterFour = "4444444444444444444444444444444444444444444444444444444444444444";
    private const string RouterAlpha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void ParseAtLeastThree_AcceptsRedundantUniqueLowercasePinsAndCanonicalizesUrls()
    {
        var endpoints = RoutedRuntimeConfiguration.ParseAtLeastThree(string.Join(';',
            $"{RouterOne}|http://127.0.0.1:29281",
            $"{RouterTwo}|http://[::1]:29282",
            $"{RouterThree}|https://router-three.example",
            $"{RouterFour}|https://router-four.example"));

        Assert.Equal(4, endpoints.Count);
        Assert.Equal([RouterOne, RouterTwo, RouterThree, RouterFour], endpoints.Select(static endpoint => endpoint.ExpectedRouterId));
        Assert.Equal("http://127.0.0.1:29281/", endpoints[0].BaseUrl);
        Assert.Equal("http://[::1]:29282/", endpoints[1].BaseUrl);
        Assert.Equal("https://router-three.example/", endpoints[2].BaseUrl);
    }

    [Theory]
    [MemberData(nameof(InvalidRouterSets))]
    public void ParseAtLeastThree_RejectsTooSmallDuplicateOrUntrustedRouterSets(string value)
    {
        Assert.Throws<InvalidOperationException>(() => RoutedRuntimeConfiguration.ParseAtLeastThree(value));
    }

    [Fact]
    public void ValidateAtLeastThree_RejectsCanonicalUrlAliases()
    {
        var endpoints = new[]
        {
            new PinnedRouterEndpoint("https://router-one.example", RouterOne),
            new PinnedRouterEndpoint("https://router-one.example/", RouterTwo),
            new PinnedRouterEndpoint("https://router-three.example", RouterThree)
        };

        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.ValidateAtLeastThree(endpoints));
    }

    [Fact]
    public void RoutedComposition_RejectsAnyDirectStorageSetting()
    {
        RoutedRuntimeConfiguration.RejectDirectStorageForRoutedComposition(null);
        RoutedRuntimeConfiguration.RejectDirectStorageForRoutedComposition(" ");

        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.RejectDirectStorageForRoutedComposition(
                "http://127.0.0.1:18100"));
    }

    [Theory]
    [InlineData("https://service.example")]
    [InlineData("http://127.0.0.1:18102")]
    public void RequireLiveServiceUrl_AcceptsHttpsOrExplicitLoopbackHttp(string value)
    {
        Assert.True(RoutedRuntimeConfiguration.RequireLiveServiceUrl("TEST_URL", value).IsAbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("http://service.example")]
    [InlineData("http://localhost:18103")]
    [InlineData("ftp://127.0.0.1/service")]
    [InlineData("https://user@service.example")]
    [InlineData("https://service.example/path?query=value")]
    [InlineData("https://service.example/#fragment")]
    public void RequireLiveServiceUrl_RejectsMissingRelativeOrCleartextRemoteUrls(string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.RequireLiveServiceUrl("TEST_URL", value));
    }

    public static TheoryData<string> InvalidRouterSets => new()
    {
        string.Join(';',
            $"{RouterOne}|https://router-one.example",
            $"{RouterTwo}|https://router-two.example"),
        string.Join(';',
            $"{RouterOne}|https://router-one.example",
            $"{RouterOne}|https://router-two.example",
            $"{RouterThree}|https://router-three.example"),
        string.Join(';',
            $"{RouterOne}|https://router-one.example",
            $"{RouterTwo}|https://router-one.example",
            $"{RouterThree}|https://router-three.example"),
        string.Join(';',
            $"{RouterAlpha.ToUpperInvariant()}|https://router-one.example",
            $"{RouterTwo}|https://router-two.example",
            $"{RouterThree}|https://router-three.example"),
        string.Join(';',
            $"{RouterOne}|http://router-one.example",
            $"{RouterTwo}|https://router-two.example",
            $"{RouterThree}|https://router-three.example")
    };

    [Fact]
    public void ParseAtLeastThree_RejectsRouterSetsAboveTheConfiguredMaximum()
    {
        var routers = Enumerable.Range(1, RoutedRuntimeConfiguration.MaximumRouterCount + 1)
            .Select(index =>
                $"{index.ToString("x64")}|https://router-{index}.example/");

        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.ParseAtLeastThree(string.Join(';', routers)));
    }

    [Theory]
    [InlineData("https://router-one.example/base")]
    [InlineData("https://user@router-one.example/")]
    [InlineData("https://router-one.example/?query=value")]
    [InlineData("https://router-one.example/#fragment")]
    [InlineData("http://localhost:29281/")]
    public void ParseAtLeastThree_RejectsNonCanonicalRouterBaseUrl(string firstUrl)
    {
        var value = string.Join(';',
            $"{RouterOne}|{firstUrl}",
            $"{RouterTwo}|https://router-two.example/",
            $"{RouterThree}|https://router-three.example/");

        Assert.Throws<InvalidOperationException>(() =>
            RoutedRuntimeConfiguration.ParseAtLeastThree(value));
    }

    [Fact]
    public async Task ProductionFactory_ResolvesRealRoutedTypesAndFailsClosedDuringRouterOutage()
    {
        var endpoints = RoutedRuntimeConfiguration.ParseAtLeastThree(string.Join(';',
            $"{RouterOne}|https://router-one.example/",
            $"{RouterTwo}|https://router-two.example/",
            $"{RouterThree}|https://router-three.example/"));
        var handler = new RouterOutageHandler(endpoints);
        var composition = RoutedProductionCompositionFactory.Create(
            endpoints,
            directStorageUrl: null,
            new HttpClient(handler),
            new RoutedSessionStorageTransportOptions(
                MetadataMode: SessionStorageMetadataMode.LegacyCompatibility));

        Assert.IsType<XNodeRpcClient>(composition.RouteProvider);
        Assert.IsType<RoutedSessionStorageMessageTransport>(composition.SessionMessageTransport);
        Assert.Same(composition.Router, composition.RouteProvider);
        Assert.Same(composition.MessageTransport, composition.SessionMessageTransport);
        Assert.Equal(endpoints, composition.PinnedRouters);

        var exception = await Record.ExceptionAsync(() =>
            composition.SessionMessageTransport.SendAsync(new OutboundMessageEnvelope(
                new SessionId("sender"),
                new SessionId("recipient"),
                "must-fail-closed",
                [],
                DateTimeOffset.UtcNow,
                null)));

        Assert.NotNull(exception);
        Assert.True(handler.RequestCount > 0);
        Assert.Empty(handler.UnexpectedDestinations);
    }

    [Fact]
    public void ProductionFactory_DefaultOpaqueModeFailsClosedWithoutExplicitP03Dependencies()
    {
        var endpoints = RoutedRuntimeConfiguration.ParseAtLeastThree(string.Join(';',
            $"{RouterOne}|https://router-one.example/",
            $"{RouterTwo}|https://router-two.example/",
            $"{RouterThree}|https://router-three.example/"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            RoutedProductionCompositionFactory.Create(
                endpoints,
                directStorageUrl: null,
                new HttpClient(),
                new RoutedSessionStorageTransportOptions()));

        Assert.Equal(
            "Opaque P03 routed storage requires explicit capability, crypto and replay dependencies.",
            error.Message);
    }

    [Fact]
    public void ProductionFactory_RejectsDirectStorageAndTooFewRouters()
    {
        var valid = RoutedRuntimeConfiguration.ParseAtLeastThree(string.Join(';',
            $"{RouterOne}|https://router-one.example/",
            $"{RouterTwo}|https://router-two.example/",
            $"{RouterThree}|https://router-three.example/"));

        Assert.Throws<InvalidOperationException>(() =>
            RoutedProductionCompositionFactory.Create(
                valid,
                "https://storage.example/",
                new HttpClient(),
                new RoutedSessionStorageTransportOptions()));
        Assert.Throws<InvalidOperationException>(() =>
            RoutedProductionCompositionFactory.Create(
                valid.Take(2),
                directStorageUrl: null,
                new HttpClient(),
                new RoutedSessionStorageTransportOptions()));
    }

    [Fact]
    public void ProductionFactory_AcceptsRedundantPinnedRouters()
    {
        var endpoints = RoutedRuntimeConfiguration.ParseAtLeastThree(string.Join(';',
            $"{RouterOne}|https://router-one.example/",
            $"{RouterTwo}|https://router-two.example/",
            $"{RouterThree}|https://router-three.example/",
            $"{RouterFour}|https://router-four.example/"));

        var composition = RoutedProductionCompositionFactory.Create(
            endpoints,
            directStorageUrl: null,
            new HttpClient(),
            new RoutedSessionStorageTransportOptions(
                MetadataMode: SessionStorageMetadataMode.LegacyCompatibility));

        Assert.Equal(endpoints, composition.PinnedRouters);
    }

    private sealed class RouterOutageHandler(
        IReadOnlyList<PinnedRouterEndpoint> endpoints) : HttpMessageHandler
    {
        private readonly HashSet<string> allowedOrigins = endpoints
            .Select(static endpoint => new Uri(endpoint.BaseUrl).GetLeftPart(UriPartial.Authority))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> unexpectedDestinations = new();
        private int requestCount;

        public int RequestCount => Volatile.Read(ref requestCount);

        public IReadOnlyList<string> UnexpectedDestinations => unexpectedDestinations.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            var destination = request.RequestUri?.GetLeftPart(UriPartial.Authority) ?? "<missing>";
            if (!allowedOrigins.Contains(destination))
            {
                unexpectedDestinations.Enqueue(destination);
            }

            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException(
                    HttpRequestError.ConnectionError,
                    "Pinned router API is unavailable.",
                    inner: null,
                    statusCode: HttpStatusCode.ServiceUnavailable));
        }
    }
}
