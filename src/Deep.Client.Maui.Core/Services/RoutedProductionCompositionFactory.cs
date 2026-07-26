using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Core.Services;

public sealed class RoutedProductionComposition : IDisposable
{
    internal RoutedProductionComposition(
        IReadOnlyList<PinnedRouterEndpoint> pinnedRouters,
        XNodeRpcClient router,
        RoutedSessionStorageMessageTransport messageTransport,
        IMembershipRouteCatalogProvider? membershipRouteCatalogProvider)
    {
        PinnedRouters = pinnedRouters;
        Router = router;
        MessageTransport = messageTransport;
        MembershipRouteCatalogProvider = membershipRouteCatalogProvider;
    }

    public IReadOnlyList<PinnedRouterEndpoint> PinnedRouters { get; }

    public XNodeRpcClient Router { get; }

    public RoutedSessionStorageMessageTransport MessageTransport { get; }

    public IMembershipRouteCatalogProvider? MembershipRouteCatalogProvider { get; }

    public ITransportRouteProvider RouteProvider => Router;

    public ISessionMessageTransport SessionMessageTransport => MessageTransport;

    public void Dispose()
    {
        if (MembershipRouteCatalogProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

public static class RoutedProductionCompositionFactory
{
    public static RoutedProductionComposition Create(
        IEnumerable<PinnedRouterEndpoint> routerEndpoints,
        string? directStorageUrl,
        HttpClient routerHttpClient,
        RoutedSessionStorageTransportOptions transportOptions,
        TimeProvider? timeProvider = null) =>
        CreateCore(
            routerEndpoints,
            directStorageUrl,
            routerHttpClient,
            transportOptions,
            membershipRouteCatalogProvider: null,
            timeProvider);

    public static RoutedProductionComposition CreateVerified(
        IEnumerable<PinnedRouterEndpoint> routerEndpoints,
        string? directStorageUrl,
        HttpClient routerHttpClient,
        RoutedSessionStorageTransportOptions transportOptions,
        IMembershipRouteCatalogProvider membershipRouteCatalogProvider,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(membershipRouteCatalogProvider);
        return CreateCore(
            routerEndpoints,
            directStorageUrl,
            routerHttpClient,
            transportOptions,
            membershipRouteCatalogProvider,
            timeProvider);
    }

    private static RoutedProductionComposition CreateCore(
        IEnumerable<PinnedRouterEndpoint> routerEndpoints,
        string? directStorageUrl,
        HttpClient routerHttpClient,
        RoutedSessionStorageTransportOptions transportOptions,
        IMembershipRouteCatalogProvider? membershipRouteCatalogProvider,
        TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(routerHttpClient);
        ArgumentNullException.ThrowIfNull(transportOptions);
        var validatedEndpoints = RoutedRuntimeConfiguration.ValidateAtLeastThree(routerEndpoints);
        RoutedRuntimeConfiguration.RejectDirectStorageForRoutedComposition(directStorageUrl);

        var router = new XNodeRpcClient(
            routerHttpClient,
            new XNodeRpcClientOptions(
                validatedEndpoints,
                RequireMembershipRouteSelection:
                    membershipRouteCatalogProvider is not null),
            timeProvider,
            membershipRouteCatalogProvider);
        var transport = new RoutedSessionStorageMessageTransport(
            router,
            transportOptions);
        return new RoutedProductionComposition(
            validatedEndpoints,
            router,
            transport,
            membershipRouteCatalogProvider);
    }
}
