using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Core.Services;

public sealed class RoutedProductionComposition
{
    internal RoutedProductionComposition(
        IReadOnlyList<PinnedRouterEndpoint> pinnedRouters,
        XNodeRpcClient router,
        RoutedSessionStorageMessageTransport messageTransport)
    {
        PinnedRouters = pinnedRouters;
        Router = router;
        MessageTransport = messageTransport;
    }

    public IReadOnlyList<PinnedRouterEndpoint> PinnedRouters { get; }

    public XNodeRpcClient Router { get; }

    public RoutedSessionStorageMessageTransport MessageTransport { get; }

    public ITransportRouteProvider RouteProvider => Router;

    public ISessionMessageTransport SessionMessageTransport => MessageTransport;
}

public static class RoutedProductionCompositionFactory
{
    public static RoutedProductionComposition Create(
        IEnumerable<PinnedRouterEndpoint> routerEndpoints,
        string? directStorageUrl,
        HttpClient routerHttpClient,
        RoutedSessionStorageTransportOptions transportOptions,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(routerHttpClient);
        ArgumentNullException.ThrowIfNull(transportOptions);
        var validatedEndpoints = RoutedRuntimeConfiguration.ValidateAtLeastThree(routerEndpoints);
        RoutedRuntimeConfiguration.RejectDirectStorageForRoutedComposition(directStorageUrl);

        var router = new XNodeRpcClient(
            routerHttpClient,
            new XNodeRpcClientOptions(validatedEndpoints),
            timeProvider);
        var transport = new RoutedSessionStorageMessageTransport(
            router,
            transportOptions);
        return new RoutedProductionComposition(validatedEndpoints, router, transport);
    }
}
