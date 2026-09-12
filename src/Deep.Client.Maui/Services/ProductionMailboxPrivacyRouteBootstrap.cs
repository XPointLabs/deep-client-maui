using System.Security.Cryptography;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.ContactV1;
using Deep.Client.Shared.Services.XPointNetworkV1;
using Deep.Protocol.DeepExtension.PrivacyRouting;
using Deep.Protocol.XPointNetworkV1;

namespace Deep.Client.Maui.Services;

/// <summary>
/// Capability-only input supplied after the deployment owner has verified its
/// signed bootstrap. MAUI receives no raw hop, routing-key, signer, or boolean
/// trust input and never parses an unsigned route document.
/// </summary>
internal sealed class ProductionContactResolveVerifiedHostCapabilities
{
    internal ProductionContactResolveVerifiedHostCapabilities(
        XPointNetworkGenesisPin genesisPin,
        IContactResolverVerifiedCapabilitySource resolverCapabilities,
        IContactResolvePathAuthoritySource pathAuthoritySource,
        IContactResolveVerifiedPrivacyIngressSource privacyIngressSource)
    {
        GenesisPin = genesisPin ?? throw new ArgumentNullException(nameof(genesisPin));
        ResolverCapabilities = resolverCapabilities
            ?? throw new ArgumentNullException(nameof(resolverCapabilities));
        PathAuthoritySource = pathAuthoritySource
            ?? throw new ArgumentNullException(nameof(pathAuthoritySource));
        PrivacyIngressSource = privacyIngressSource
            ?? throw new ArgumentNullException(nameof(privacyIngressSource));
    }

    internal XPointNetworkGenesisPin GenesisPin { get; }
    internal IContactResolverVerifiedCapabilitySource ResolverCapabilities { get; }
    internal IContactResolvePathAuthoritySource PathAuthoritySource { get; }
    internal IContactResolveVerifiedPrivacyIngressSource PrivacyIngressSource { get; }
}

internal interface IProductionContactResolveVerifiedHostCapabilitiesSource
{
    ValueTask<ProductionContactResolveVerifiedHostCapabilities?> GetCurrentAsync(
        CancellationToken cancellationToken);
}

internal sealed class FixedProductionContactResolveVerifiedHostCapabilitiesSource(
    ProductionContactResolveVerifiedHostCapabilities? value)
    : IProductionContactResolveVerifiedHostCapabilitiesSource
{
    public ValueTask<ProductionContactResolveVerifiedHostCapabilities?> GetCurrentAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(value);
    }
}

/// <summary>
/// Deployment-owned capability which may return origins only after its signed
/// bootstrap and network binding have been verified. Raw route documents never
/// enter the MAUI composition boundary.
/// </summary>
internal interface IContactResolveVerifiedPrivacyIngressSource
{
    ValueTask<VerifiedContactResolvePrivacyIngressPair> GetCurrentAsync(
        CancellationToken cancellationToken);
}

internal sealed class VerifiedContactResolvePrivacyIngressPair
{
    private readonly byte[] networkId;
    private readonly byte[] primaryRouterId;
    private readonly byte[] fallbackRouterId;

    internal VerifiedContactResolvePrivacyIngressPair(
        ReadOnlyMemory<byte> networkId,
        Uri primaryOrigin,
        Uri fallbackOrigin)
        : this(
            networkId,
            primaryOrigin,
            ReadOnlyMemory<byte>.Empty,
            fallbackOrigin,
            ReadOnlyMemory<byte>.Empty)
    {
    }

    internal VerifiedContactResolvePrivacyIngressPair(
        ReadOnlyMemory<byte> networkId,
        Uri primaryOrigin,
        ReadOnlyMemory<byte> primaryRouterId,
        Uri fallbackOrigin,
        ReadOnlyMemory<byte> fallbackRouterId)
    {
        if (networkId.Length != 16
            || networkId.Span.IndexOfAnyExcept((byte)0) < 0)
        {
            throw new ArgumentException("A verified ingress network ID is required.",
                nameof(networkId));
        }
        ValidateOrigin(primaryOrigin, nameof(primaryOrigin));
        ValidateOrigin(fallbackOrigin, nameof(fallbackOrigin));
        ValidateRouterId(primaryRouterId, nameof(primaryRouterId));
        ValidateRouterId(fallbackRouterId, nameof(fallbackRouterId));
        this.networkId = networkId.ToArray();
        this.primaryRouterId = primaryRouterId.ToArray();
        this.fallbackRouterId = fallbackRouterId.ToArray();
        PrimaryOrigin = primaryOrigin;
        FallbackOrigin = fallbackOrigin;
    }

    internal ReadOnlyMemory<byte> NetworkId => networkId.ToArray();
    internal Uri PrimaryOrigin { get; }
    internal ReadOnlyMemory<byte> PrimaryRouterId => primaryRouterId.ToArray();
    internal Uri FallbackOrigin { get; }
    internal ReadOnlyMemory<byte> FallbackRouterId => fallbackRouterId.ToArray();

    private static void ValidateOrigin(Uri origin, string name)
    {
        ArgumentNullException.ThrowIfNull(origin, name);
        if (!origin.IsAbsoluteUri
            || origin.Scheme != Uri.UriSchemeHttps
                && origin.Scheme != Uri.UriSchemeHttp
            || !string.IsNullOrEmpty(origin.UserInfo)
            || origin.AbsolutePath != "/"
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment)
            || origin.Scheme == Uri.UriSchemeHttp && !origin.IsLoopback)
        {
            throw new ArgumentException(
                "A verified privacy ingress must be clean HTTPS or an explicit loopback HTTP origin.", name);
        }
    }

    private static void ValidateRouterId(ReadOnlyMemory<byte> routerId, string name)
    {
        if (!routerId.IsEmpty &&
            (routerId.Length != 32 || routerId.Span.IndexOfAnyExcept((byte)0) < 0))
        {
            throw new ArgumentException(
                "A verified ingress router ID must be empty or exactly 32 non-zero bytes.",
                name);
        }
    }
}

internal interface IProductionContactResolveHostOptionsSource
{
    ValueTask<ProductionContactResolveHostOptions?> GetCurrentAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Default-dormant clean-break host bootstrap. With no verified capabilities it
/// returns before opening account state. Once configured, it binds the verified
/// sources to the current account/device protected guard, entropy, and key owners.
/// </summary>
internal sealed class ProductionMailboxPrivacyRouteBootstrap :
    IProductionContactResolveHostOptionsSource
{
    internal const string UnavailableCode =
        "production-privacy-routing-host-adapters-unavailable";

    private readonly DeepAccountRuntimeAccessor accounts;
    private readonly IProductionContactResolveVerifiedHostCapabilitiesSource capabilitySource;
    private readonly Func<ReadOnlyMemory<byte>> activeNetworkIdFactory;
    private readonly SemaphoreSlim gate = new(1, 1);

    internal ProductionMailboxPrivacyRouteBootstrap(
        DeepAccountRuntimeAccessor accounts,
        ProductionContactResolveVerifiedHostCapabilities? capabilities,
        Func<ReadOnlyMemory<byte>> activeNetworkIdFactory)
        : this(
            accounts,
            new FixedProductionContactResolveVerifiedHostCapabilitiesSource(capabilities),
            activeNetworkIdFactory)
    {
    }

    internal ProductionMailboxPrivacyRouteBootstrap(
        DeepAccountRuntimeAccessor accounts,
        IProductionContactResolveVerifiedHostCapabilitiesSource capabilitySource,
        Func<ReadOnlyMemory<byte>> activeNetworkIdFactory)
    {
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.capabilitySource = capabilitySource
            ?? throw new ArgumentNullException(nameof(capabilitySource));
        this.activeNetworkIdFactory = activeNetworkIdFactory
            ?? throw new ArgumentNullException(nameof(activeNetworkIdFactory));
    }

    // Mailbox runtime still needs its separate receive/replay host adapters.
    // ContactResolve client activation is owned by GetCurrentAsync below.
    internal static bool HostAdaptersAvailable => false;

    internal static void RequireHostAdapters()
    {
        if (!HostAdaptersAvailable)
        {
            throw CreateUnavailableException();
        }
    }

    internal static InvalidOperationException CreateUnavailableException() =>
        new(UnavailableCode);

    public async ValueTask<ProductionContactResolveHostOptions?> GetCurrentAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capabilities = await capabilitySource.GetCurrentAsync(cancellationToken)
            .ConfigureAwait(false);
        if (capabilities is null)
        {
            return null;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var activeNetworkId = activeNetworkIdFactory().ToArray();
            try
            {
                if (activeNetworkId.Length != 16
                    || activeNetworkId.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                {
                    throw new ContactResolveHostBootstrapException(
                        ContactResolveRuntimeUnavailableReason.MalformedConfiguration,
                        "The active ContactResolve network binding is malformed.");
                }
                if (!Fixed(capabilities.GenesisPin.NetworkId.Span, activeNetworkId))
                {
                    throw new ContactResolveHostBootstrapException(
                        ContactResolveRuntimeUnavailableReason.CrossNetworkConfiguration,
                        "The verified ContactResolve bootstrap belongs to another network.");
                }

                var account = await accounts.GetContactResolvePrivacyHostBindingAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!Fixed(account.NetworkId.Span, activeNetworkId))
                {
                    throw new ContactResolveHostBootstrapException(
                        ContactResolveRuntimeUnavailableReason.CrossNetworkConfiguration,
                        "The current account belongs to another ContactResolve network.");
                }

                var ingress = await capabilities.PrivacyIngressSource
                    .GetCurrentAsync(cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new ContactResolveHostBootstrapException(
                        ContactResolveRuntimeUnavailableReason.PrivacyRoute,
                        "The verified ContactResolve privacy ingress is unavailable.");
                if (!Fixed(ingress.NetworkId.Span, activeNetworkId))
                {
                    throw new ContactResolveHostBootstrapException(
                        ContactResolveRuntimeUnavailableReason.CrossNetworkConfiguration,
                        "The verified privacy ingress belongs to another network.");
                }

                var pathProvider = new ContactResolvePrivacyPathProvider(
                    capabilities.PathAuthoritySource,
                    account.EntryGuardStore);
                var primary = new PrivacyMailboxRoute(
                    ingress.PrimaryOrigin,
                    pathProvider,
                    ingress.PrimaryRouterId);
                var fallback = new PrivacyMailboxRoute(
                    ingress.FallbackOrigin,
                    pathProvider,
                    ingress.FallbackRouterId);
                var codec = new PrivacyRoutingCodec(
                    new OnionEntropyAuthority(account.EntropyLedger),
                    new OnionKeyAgreementAuthority(account.KeyAgreementVault));
                var verifier = new ContactResolverTrustedVerifier(
                    capabilities.ResolverCapabilities);
                return new ProductionContactResolveHostOptions(
                    () => capabilities.GenesisPin,
                    () => verifier,
                    () => primary,
                    () => fallback,
                    () => codec,
                    () => capabilities.PathAuthoritySource as IContactResolvePlacementContextSource);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(activeNetworkId);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool Fixed(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(left, right);
}

internal sealed class ContactResolveHostBootstrapException(
    ContactResolveRuntimeUnavailableReason reason,
    string message,
    Exception? inner = null) : CryptographicException(message, inner)
{
    internal ContactResolveRuntimeUnavailableReason Reason { get; } = reason;
}
