using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Core.Services;

public sealed class RoutedProductionComposition
{
    internal RoutedProductionComposition(
        XNodeRpcClient router,
        RoutedSessionStorageMessageTransport messageTransport)
    {
        Router = router;
        MessageTransport = messageTransport;
    }

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
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(routerHttpClient);
        var validatedEndpoints = RoutedRuntimeConfiguration.ValidateExactlyThree(routerEndpoints);
        RoutedRuntimeConfiguration.RejectDirectStorageForRoutedComposition(directStorageUrl);

        var router = new XNodeRpcClient(
            routerHttpClient,
            new XNodeRpcClientOptions(validatedEndpoints),
            timeProvider);
        var transport = new RoutedSessionStorageMessageTransport(
            router,
            new RoutedSessionStorageTransportOptions());
        return new RoutedProductionComposition(router, transport);
    }
}
