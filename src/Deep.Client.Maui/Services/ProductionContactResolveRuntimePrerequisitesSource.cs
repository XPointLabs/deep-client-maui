using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.ContactV1;
using Deep.Client.Shared.Services.XPointNetworkV1;
using Deep.Protocol.DeepExtension.PrivacyRouting;
using Deep.Protocol.XPointNetworkV1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deep.Client.Maui.Services;

/// <summary>
/// Strict capability boundary supplied by the deployment/bootstrap owner.
/// Routes can mint only protocol-verified attempts; no hop, key, proof, or
/// caller-authored trust decision crosses this boundary.
/// </summary>
internal sealed record ProductionContactResolveHostOptions(
    Func<XPointNetworkGenesisPin?> GenesisPinFactory,
    Func<ContactResolverTrustedVerifier?> TrustedXis1VerifierFactory,
    Func<PrivacyMailboxRoute?> PrimaryPrivacyMailboxRouteFactory,
    Func<PrivacyMailboxRoute?> FallbackPrivacyMailboxRouteFactory,
    Func<PrivacyRoutingCodec?> PrivacyRoutingCodecFactory,
    Func<IContactResolvePlacementContextSource?> PlacementContextSourceFactory,
    ushort SupportedDirectoryReader = 1);

/// <summary>
/// Production-capable but dormant unless every bootstrap-owned capability is
/// explicitly installed. No factory is evaluated at app startup.
/// </summary>
internal sealed class ProductionContactResolveRuntimePrerequisitesSource
    : IContactResolveRuntimePrerequisitesSource
{
    private readonly IProductionContactResolveHostOptionsSource hostSource;
    private readonly Func<ReadOnlyMemory<byte>> activeNetworkIdFactory;
    private readonly Func<IOnionMonotonicClock?> monotonicClockFactory;
    private readonly Func<HttpServiceTransportFactory> serviceTransportFactory;
    private readonly Func<HttpServiceClientOptions?> serviceTransportOptionsFactory;

    internal ProductionContactResolveRuntimePrerequisitesSource(
        ProductionContactResolveHostOptions? host,
        Func<ReadOnlyMemory<byte>> activeNetworkIdFactory,
        Func<IOnionMonotonicClock?> monotonicClockFactory,
        Func<HttpServiceTransportFactory> serviceTransportFactory,
        Func<HttpServiceClientOptions?> serviceTransportOptionsFactory)
        : this(
            new FixedContactResolveHostOptionsSource(host),
            activeNetworkIdFactory,
            monotonicClockFactory,
            serviceTransportFactory,
            serviceTransportOptionsFactory)
    {
    }

    internal ProductionContactResolveRuntimePrerequisitesSource(
        IProductionContactResolveHostOptionsSource hostSource,
        Func<ReadOnlyMemory<byte>> activeNetworkIdFactory,
        Func<IOnionMonotonicClock?> monotonicClockFactory,
        Func<HttpServiceTransportFactory> serviceTransportFactory,
        Func<HttpServiceClientOptions?> serviceTransportOptionsFactory)
    {
        this.hostSource = hostSource ?? throw new ArgumentNullException(nameof(hostSource));
        this.activeNetworkIdFactory = activeNetworkIdFactory
            ?? throw new ArgumentNullException(nameof(activeNetworkIdFactory));
        this.monotonicClockFactory = monotonicClockFactory
            ?? throw new ArgumentNullException(nameof(monotonicClockFactory));
        this.serviceTransportFactory = serviceTransportFactory
            ?? throw new ArgumentNullException(nameof(serviceTransportFactory));
        this.serviceTransportOptionsFactory = serviceTransportOptionsFactory
            ?? throw new ArgumentNullException(nameof(serviceTransportOptionsFactory));
    }

    public async ValueTask<ContactResolveRuntimePrerequisites> GetCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProductionContactResolveHostOptions? host;
        try
        {
            host = await hostSource.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ContactResolveHostBootstrapException exception)
        {
            return Unavailable(exception.Reason);
        }
        if (host is null)
        {
            return Unavailable(ContactResolveRuntimeUnavailableReason.GenesisPin);
        }

        try
        {
            if (host.GenesisPinFactory is null
                || host.TrustedXis1VerifierFactory is null
                || host.PrimaryPrivacyMailboxRouteFactory is null
                || host.FallbackPrivacyMailboxRouteFactory is null
                || host.PrivacyRoutingCodecFactory is null
                || host.PlacementContextSourceFactory is null
                || host.SupportedDirectoryReader == 0)
            {
                return Unavailable(ContactResolveRuntimeUnavailableReason.MalformedConfiguration);
            }

            var activeNetworkId = activeNetworkIdFactory().ToArray();
            if (activeNetworkId.Length != 16
                || activeNetworkId.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                return Unavailable(ContactResolveRuntimeUnavailableReason.MalformedConfiguration);
            }

            var genesis = host.GenesisPinFactory();
            if (genesis is null)
            {
                return Unavailable(ContactResolveRuntimeUnavailableReason.GenesisPin);
            }
            if (!genesis.NetworkId.Span.SequenceEqual(activeNetworkId))
            {
                return Unavailable(ContactResolveRuntimeUnavailableReason.CrossNetworkConfiguration);
            }

            IOnionMonotonicClock? monotonicClock;
            try
            {
                monotonicClock = monotonicClockFactory();
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException
                    or NotSupportedException)
            {
                return Unavailable(ContactResolveRuntimeUnavailableReason.MonotonicClock);
            }
            if (monotonicClock is null)
            {
                return Unavailable(ContactResolveRuntimeUnavailableReason.MonotonicClock);
            }

            var primaryRoute = host.PrimaryPrivacyMailboxRouteFactory();
            var fallbackRoute = host.FallbackPrivacyMailboxRouteFactory();
            var codec = host.PrivacyRoutingCodecFactory();
            if (primaryRoute is null || fallbackRoute is null || codec is null)
            {
                return Unavailable(ContactResolveRuntimeUnavailableReason.PrivacyRoute);
            }
            if (ReferenceEquals(primaryRoute, fallbackRoute)
                || SameOrigin(primaryRoute.EntryOrigin, fallbackRoute.EntryOrigin)
                && (primaryRoute.ExpectedEntryRouterId.IsEmpty
                    || fallbackRoute.ExpectedEntryRouterId.IsEmpty
                    || !primaryRoute.ExpectedEntryRouterId.Span.SequenceEqual(
                        fallbackRoute.ExpectedEntryRouterId.Span)))
            {
                return Unavailable(ContactResolveRuntimeUnavailableReason.MalformedConfiguration);
            }

            var verifier = host.TrustedXis1VerifierFactory();
            if (verifier is null)
            {
                return Unavailable(ContactResolveRuntimeUnavailableReason.AuthoritySource);
            }

            return new ContactResolveRuntimePrerequisites(
                genesis,
                monotonicClock,
                () => serviceTransportFactory()
                    .CreatePrivacyRoutedContactResolverTransport(
                        primaryRoute,
                        fallbackRoute,
                        codec,
                        serviceTransportOptionsFactory()),
                () => verifier,
                host.PlacementContextSourceFactory,
                host.SupportedDirectoryReader);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or FormatException
                or NotSupportedException)
        {
            return Unavailable(ContactResolveRuntimeUnavailableReason.MalformedConfiguration);
        }
    }

    private static ContactResolveRuntimePrerequisites Unavailable(
        ContactResolveRuntimeUnavailableReason reason) =>
        new(
            GenesisPin: null,
            MonotonicClock: null,
            PrivacyRoutedTransportFactory: null,
            TrustedAuthorityVerifierFactory: null,
            PlacementContextSourceFactory: null,
            UnavailableReason: reason);

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;
}

internal sealed class FixedContactResolveHostOptionsSource(
    ProductionContactResolveHostOptions? value)
    : IProductionContactResolveHostOptionsSource
{
    public ValueTask<ProductionContactResolveHostOptions?> GetCurrentAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(value);
    }
}

internal static class ContactResolveRuntimeServiceCollectionExtensions
{
    internal static IServiceCollection AddProductionContactResolveRuntimePrerequisites(
        this IServiceCollection services,
        ProductionContactResolveHostOptions? host,
        Func<ReadOnlyMemory<byte>>? activeNetworkIdFactory = null,
        Func<IServiceProvider, IProductionContactResolveHostOptionsSource>?
            hostOptionsSourceFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IPlatformMonotonicSource>(
            static _ => PlatformMonotonicSourceFactory.Create());
        services.TryAddSingleton<IOnionMonotonicClock>(serviceProvider =>
            new PlatformOnionMonotonicClock(
                serviceProvider.GetRequiredService<IPlatformMonotonicSource>()));
        services.AddSingleton<IContactResolveRuntimePrerequisitesSource>(serviceProvider =>
            new ProductionContactResolveRuntimePrerequisitesSource(
                hostOptionsSourceFactory is null
                    ? new FixedContactResolveHostOptionsSource(host)
                    : hostOptionsSourceFactory(serviceProvider),
                activeNetworkIdFactory ?? ActiveBuildNetworkId.Load,
                () => serviceProvider.GetRequiredService<IOnionMonotonicClock>(),
                () => serviceProvider.GetRequiredService<HttpServiceTransportFactory>(),
                () => serviceProvider.GetService<HttpServiceClientOptions>()));
        return services;
    }
}
