using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui;

public enum RealityTransportEndpointSource
{
    None = 0,
    Configured = 1,
    Embedded = 2
}

/// <summary>
/// Owns the local Reality sidecar for one application service container.
/// Reading <see cref="RouterEndpoints"/> is deliberately side-effect free: native
/// networking starts only when the application bootstrapper or a routed request asks for it.
/// </summary>
public interface IRealityTransportRuntime : IAsyncDisposable
{
    IReadOnlyList<RealityRouterEndpoint> RouterEndpoints { get; }

    RealityTransportEndpointSource EndpointSource { get; }

    Task EnsureStartedAsync(CancellationToken cancellationToken = default);

    Task WaitUntilReadyAsync(Uri? requestUri, CancellationToken cancellationToken);

    /// <summary>Updates the platform lifecycle state without starting native work.</summary>
    void SetForeground(bool isForeground);

    /// <summary>Invalidates listener readiness after an OS network transition.</summary>
    void NotifyNetworkChanged();

    /// <summary>Re-establishes readiness on foreground only; it never creates a background polling loop.</summary>
    Task OnForegroundAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

internal static class RealityTransportRuntimeFactory
{
    public static IRealityTransportRuntime CreateEmbedded()
    {
#if ANDROID
        return new AndroidRealityTransportRuntime();
#elif WINDOWS
        return new WindowsRealityTransport();
#else
        return new UnsupportedRealityTransportRuntime();
#endif
    }
}

internal sealed record RealityTransportBinding(
    IRealityTransportRuntime Runtime,
    IReadOnlyList<RealityRouterEndpoint> RouterEndpoints);

internal static class RealityTransportBindingResolver
{
    internal static RealityTransportBinding Resolve(
        string? configuredRouterCatalog,
        RoutedRuntimeEndpointPolicy endpointPolicy,
        Func<IRealityTransportRuntime> createEmbeddedRuntime)
    {
        ArgumentNullException.ThrowIfNull(endpointPolicy);
        ArgumentNullException.ThrowIfNull(createEmbeddedRuntime);
        if (!string.IsNullOrWhiteSpace(configuredRouterCatalog))
        {
            var configured = RoutedRuntimeConfiguration.ParseAtLeastThree(
                configuredRouterCatalog,
                endpointPolicy);
            return new RealityTransportBinding(
                new ConfiguredRealityTransportRuntime(configured),
                configured);
        }

        var runtime = createEmbeddedRuntime();
        var endpoints = runtime.RouterEndpoints.Count == 0
            ? []
            : RoutedRuntimeConfiguration.ValidateAtLeastThree(runtime.RouterEndpoints, endpointPolicy);
        return new RealityTransportBinding(runtime, endpoints);
    }
}

internal sealed class ConfiguredRealityTransportRuntime(
    IReadOnlyList<RealityRouterEndpoint> routerEndpoints) : IRealityTransportRuntime
{
    private readonly IReadOnlyList<RealityRouterEndpoint> endpoints =
        routerEndpoints?.ToArray() ?? throw new ArgumentNullException(nameof(routerEndpoints));

    public IReadOnlyList<RealityRouterEndpoint> RouterEndpoints => endpoints;

    public RealityTransportEndpointSource EndpointSource => RealityTransportEndpointSource.Configured;

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

internal sealed class UnsupportedRealityTransportRuntime : IRealityTransportRuntime
{
    public IReadOnlyList<RealityRouterEndpoint> RouterEndpoints => [];

    public RealityTransportEndpointSource EndpointSource => RealityTransportEndpointSource.None;

    public Task EnsureStartedAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task WaitUntilReadyAsync(Uri? requestUri, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public void SetForeground(bool isForeground)
    {
    }

    public void NotifyNetworkChanged()
    {
    }

    public Task OnForegroundAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
