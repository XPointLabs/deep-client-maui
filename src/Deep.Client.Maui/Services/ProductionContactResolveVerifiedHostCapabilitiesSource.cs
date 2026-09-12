using System.Security.Cryptography;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.ContactV1;
using Deep.Protocol.DeepExtension.PrivacyRouting;

namespace Deep.Client.Maui.Services;

/// <summary>
/// Binds immutable production trust, the account's protected rollback stores,
/// and the app-owned Reality ingress catalog into ContactV1 capabilities. The
/// source caches one path authority per open account-store generation because
/// its verified in-process predecessor capability is intentionally not
/// serializable.
/// </summary>
internal sealed class ProductionContactResolveVerifiedHostCapabilitiesSource :
    IProductionContactResolveVerifiedHostCapabilitiesSource,
    IDisposable
{
    private readonly DeepAccountRuntimeAccessor accounts;
    private readonly HttpServiceTransportFactory transportFactory;
    private readonly HttpServiceClientOptions clientOptions;
    private readonly IOnionMonotonicClock monotonicClock;
    private readonly string? registryUrl;
    private readonly IRealityTransportRuntime reality;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<IDisposable> ownedAuthorities = [];
    private CachedCapabilities? cached;
    private int disposed;

    internal ProductionContactResolveVerifiedHostCapabilitiesSource(
        DeepAccountRuntimeAccessor accounts,
        HttpServiceTransportFactory transportFactory,
        HttpServiceClientOptions clientOptions,
        IOnionMonotonicClock monotonicClock,
        string? registryUrl,
        IRealityTransportRuntime reality)
    {
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.transportFactory = transportFactory
            ?? throw new ArgumentNullException(nameof(transportFactory));
        this.clientOptions = clientOptions
            ?? throw new ArgumentNullException(nameof(clientOptions));
        this.monotonicClock = monotonicClock
            ?? throw new ArgumentNullException(nameof(monotonicClock));
        this.registryUrl = registryUrl;
        this.reality = reality ?? throw new ArgumentNullException(nameof(reality));
    }

    public async ValueTask<ProductionContactResolveVerifiedHostCapabilities?>
        GetCurrentAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        var genesisPin = ActiveBuildXPointGenesisPin.TryLoad();
        if (genesisPin is null || string.IsNullOrWhiteSpace(registryUrl))
        {
            return null;
        }

        var endpoints = reality.RouterEndpoints;
        if (endpoints.Count == 0)
        {
            return null;
        }

        var ingress = BindIngress(genesisPin.NetworkId, endpoints[0]);
        var persistence = await accounts.GetContactResolvePersistenceBindingAsync(
                cancellationToken)
            .ConfigureAwait(false);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (cached is not null &&
                ReferenceEquals(cached.AccountDirectoryStore,
                    persistence.AccountDirectoryStore) &&
                ReferenceEquals(cached.XPointNetworkStore,
                    persistence.XPointNetworkStore))
            {
                return cached.Capabilities;
            }

            var authority = transportFactory
                .CreateProductionContactResolvePathAuthoritySource(
                    registryUrl,
                    genesisPin,
                    persistence.AccountDirectoryStore,
                    persistence.XPointNetworkStore,
                    monotonicClock,
                    supportedDirectoryReader: 1,
                    clientOptions);
            lock (ownedAuthorities)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    authority.Dispose();
                    throw new ObjectDisposedException(nameof(
                        ProductionContactResolveVerifiedHostCapabilitiesSource));
                }
                ownedAuthorities.Add(authority);
            }
            var capabilities = new ProductionContactResolveVerifiedHostCapabilities(
                genesisPin,
                new ProductionContactResolverVerifiedCapabilitySource(authority),
                authority,
                new FixedVerifiedPrivacyIngressSource(ingress));
            cached = new CachedCapabilities(
                persistence.AccountDirectoryStore,
                persistence.XPointNetworkStore,
                capabilities);
            return capabilities;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lock (ownedAuthorities)
        {
            foreach (var authority in ownedAuthorities)
            {
                authority.Dispose();
            }
            ownedAuthorities.Clear();
            cached = null;
        }
    }

    private static VerifiedContactResolvePrivacyIngressPair BindIngress(
        ReadOnlyMemory<byte> networkId,
        RealityRouterEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        byte[] routerId;
        try
        {
            routerId = Convert.FromHexString(endpoint.ExpectedRouterId);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException(
                "The production Reality ingress router ID is malformed.",
                exception);
        }

        try
        {
            var origin = new Uri(endpoint.BaseUrl, UriKind.Absolute);
            // The first release has one signed Ingress-role seed. Primary and
            // fallback therefore share its app-owned carrier until an
            // independently verified second ingress is published.
            return new VerifiedContactResolvePrivacyIngressPair(
                networkId,
                origin,
                routerId,
                origin,
                routerId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(routerId);
        }
    }

    private sealed record CachedCapabilities(
        Deep.Client.Shared.Persistence.AccountDirectoryV1.IAccountDirectoryStateStore
            AccountDirectoryStore,
        Deep.Client.Shared.Persistence.XPointNetworkV1.IXPointNetworkStateStore
            XPointNetworkStore,
        ProductionContactResolveVerifiedHostCapabilities Capabilities);

    private sealed class FixedVerifiedPrivacyIngressSource(
        VerifiedContactResolvePrivacyIngressPair value)
        : IContactResolveVerifiedPrivacyIngressSource
    {
        public ValueTask<VerifiedContactResolvePrivacyIngressPair> GetCurrentAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(value);
        }
    }
}
