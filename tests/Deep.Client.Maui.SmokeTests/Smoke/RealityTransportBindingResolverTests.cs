using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class RealityTransportBindingResolverTests
{
    [Fact]
    public void ConfiguredCatalogIsAuthoritativeAndNeverConstructsEmbeddedSidecar()
    {
        var embeddedFactoryCalls = 0;
        var configured = string.Join(';',
            Entry('a', "https://router-a.example"),
            Entry('b', "https://router-b.example"),
            Entry('c', "https://router-c.example"));

        var binding = RealityTransportBindingResolver.Resolve(
            configured,
            RoutedRuntimeEndpointPolicy.Production,
            () =>
            {
                embeddedFactoryCalls++;
                return new FakeRuntime(RealityTransportEndpointSource.Embedded, []);
            });

        Assert.Equal(0, embeddedFactoryCalls);
        Assert.Equal(RealityTransportEndpointSource.Configured, binding.Runtime.EndpointSource);
        Assert.Equal(3, binding.RouterEndpoints.Count);
        Assert.Equal(
            binding.RouterEndpoints.Select(static endpoint => endpoint.BaseUrl),
            binding.Runtime.RouterEndpoints.Select(static endpoint => endpoint.BaseUrl));
    }

    [Fact]
    public void EmbeddedModePublishesOnlyTheRuntimeCatalog()
    {
        var runtimeEndpoints = new[]
        {
            new PinnedRouterEndpoint("http://127.0.0.1:51001", new string('a', 64)),
            new PinnedRouterEndpoint("http://127.0.0.1:51002", new string('b', 64)),
            new PinnedRouterEndpoint("http://127.0.0.1:51003", new string('c', 64))
        };

        var binding = RealityTransportBindingResolver.Resolve(
            configuredRouterCatalog: null,
            RoutedRuntimeEndpointPolicy.Production,
            () => new FakeRuntime(RealityTransportEndpointSource.Embedded, runtimeEndpoints));

        Assert.Equal(RealityTransportEndpointSource.Embedded, binding.Runtime.EndpointSource);
        Assert.Equal(
            runtimeEndpoints.Select(static endpoint => endpoint.ExpectedRouterId),
            binding.RouterEndpoints.Select(static endpoint => endpoint.ExpectedRouterId));
        Assert.Equal(
            runtimeEndpoints.Select(static endpoint => new Uri(endpoint.BaseUrl).Port),
            binding.RouterEndpoints.Select(static endpoint => new Uri(endpoint.BaseUrl).Port));
    }

    private static string Entry(char routerIdDigit, string url) =>
        $"{new string(routerIdDigit, 64)}|{url}";

    private sealed class FakeRuntime(
        RealityTransportEndpointSource endpointSource,
        IReadOnlyList<PinnedRouterEndpoint> endpoints) : IRealityTransportRuntime
    {
        public IReadOnlyList<PinnedRouterEndpoint> RouterEndpoints { get; } = endpoints;

        public RealityTransportEndpointSource EndpointSource { get; } = endpointSource;

        public Task EnsureStartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WaitUntilReadyAsync(Uri? requestUri, CancellationToken cancellationToken) => Task.CompletedTask;

        public void SetForeground(bool isForeground)
        {
        }

        public void NotifyNetworkChanged()
        {
        }

        public Task OnForegroundAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
