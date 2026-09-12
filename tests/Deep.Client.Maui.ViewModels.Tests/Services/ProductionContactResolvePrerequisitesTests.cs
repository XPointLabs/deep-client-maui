using Deep.Client.Maui.Services;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.ContactV1;
using Deep.Client.Shared.Services.XPointNetworkV1;
using Deep.Protocol.ContactV1;
using Deep.Protocol.DeepExtension.PrivacyRouting;
using Deep.Protocol.XPointNetworkV1;
using Microsoft.Extensions.DependencyInjection;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class ProductionContactResolvePrerequisitesTests
{
    private static readonly byte[] NetworkId = Enumerable.Range(1, 16)
        .Select(static value => (byte)value).ToArray();

    [Fact]
    public async Task NoHostConfigurationIsDormantAndDoesNotResolveNetworkServices()
    {
        var environmentReads = 0;
        var services = new ServiceCollection();
        services.AddProductionContactResolveRuntimePrerequisites(
            host: null,
            activeNetworkIdFactory: () =>
            {
                environmentReads++;
                throw new InvalidOperationException("must remain lazy");
            });
        using var provider = services.BuildServiceProvider();

        var current = await provider
            .GetRequiredService<IContactResolveRuntimePrerequisitesSource>()
            .GetCurrentAsync();

        Assert.Equal(ContactResolveRuntimeUnavailableReason.GenesisPin,
            current.UnavailableReason);
        Assert.Equal(0, environmentReads);
    }

    [Fact]
    public async Task CompleteHostConfigurationActivatesOnlyTypedCapabilityFactories()
    {
        var services = new ServiceCollection();
        var platform = new MutablePlatformSource(Bytes(16, 0x61), 50);
        services.AddSingleton<IPlatformMonotonicSource>(platform);
        services.AddSingleton(new HttpServiceTransportFactory(
            HttpServiceEndpointPolicy.Production));
        services.AddProductionContactResolveRuntimePrerequisites(
            CompleteHost(NetworkId),
            () => NetworkId.ToArray());
        using var provider = services.BuildServiceProvider();

        var current = await provider
            .GetRequiredService<IContactResolveRuntimePrerequisitesSource>()
            .GetCurrentAsync();

        Assert.Null(current.UnavailableReason);
        Assert.NotNull(current.GenesisPin);
        Assert.NotNull(current.MonotonicClock);
        Assert.NotNull(current.PrivacyRoutedTransportFactory);
        Assert.NotNull(current.TrustedAuthorityVerifierFactory);
        Assert.NotNull(current.PlacementContextSourceFactory);
        Assert.NotNull(current.PlacementContextSourceFactory!());
        Assert.Equal((ushort)1, current.SupportedDirectoryReader);
        Assert.Equal(NetworkId, current.GenesisPin!.NetworkId.ToArray());
    }

    [Fact]
    public async Task DiHostSourceFactoryCanInstallVerifiedUatCompositionLazily()
    {
        var hostSourceReads = 0;
        var services = new ServiceCollection();
        services.AddSingleton<IPlatformMonotonicSource>(
            new MutablePlatformSource(Bytes(16, 0x62), 51));
        services.AddSingleton(new HttpServiceTransportFactory(
            HttpServiceEndpointPolicy.Production));
        services.AddProductionContactResolveRuntimePrerequisites(
            host: null,
            activeNetworkIdFactory: () => NetworkId.ToArray(),
            hostOptionsSourceFactory: _ => new DelegateHostSource(() =>
            {
                hostSourceReads++;
                return CompleteHost(NetworkId);
            }));
        using var provider = services.BuildServiceProvider();
        Assert.Equal(0, hostSourceReads);

        var current = await provider
            .GetRequiredService<IContactResolveRuntimePrerequisitesSource>()
            .GetCurrentAsync();

        Assert.Null(current.UnavailableReason);
        Assert.Equal(1, hostSourceReads);
    }

    [Fact]
    public async Task SameOriginRoutesAreRejectedAsMalformedBeforeTransportCreation()
    {
        var transportFactoryReads = 0;
        var sameOrigin = new Uri("https://same.example/");
        var host = CompleteHost(
            NetworkId,
            () => Route(sameOrigin),
            () => Route(sameOrigin));
        var source = Source(host, () =>
        {
            transportFactoryReads++;
            return new HttpServiceTransportFactory(HttpServiceEndpointPolicy.Production);
        });

        var current = await source.GetCurrentAsync();

        Assert.Equal(ContactResolveRuntimeUnavailableReason.MalformedConfiguration,
            current.UnavailableReason);
        Assert.Equal(0, transportFactoryReads);
    }

    [Fact]
    public async Task CrossNetworkGenesisIsRejectedBeforeRouteAndVerifierFactories()
    {
        var sensitiveFactoryReads = 0;
        var host = new ProductionContactResolveHostOptions(
            () => new XPointNetworkGenesisPin(Bytes(16, 0xE1), Bytes(32, 0xE2)),
            () =>
            {
                sensitiveFactoryReads++;
                return TrustedVerifier();
            },
            () =>
            {
                sensitiveFactoryReads++;
                return Route(new Uri("https://primary.example/"));
            },
            () =>
            {
                sensitiveFactoryReads++;
                return Route(new Uri("https://fallback.example/"));
            },
            () =>
            {
                sensitiveFactoryReads++;
                return Codec();
            },
            () => new NoUsePlacementSource());
        var source = Source(host);

        var current = await source.GetCurrentAsync();

        Assert.Equal(ContactResolveRuntimeUnavailableReason.CrossNetworkConfiguration,
            current.UnavailableReason);
        Assert.Equal(0, sensitiveFactoryReads);
    }

    [Fact]
    public async Task ThrowingHostFactoryIsRejectedAsMalformedWithoutRawProofFallback()
    {
        var host = CompleteHost(
            NetworkId,
            () => throw new FormatException("malformed bootstrap route"),
            () => Route(new Uri("https://fallback.example/")));
        var source = Source(host);

        var current = await source.GetCurrentAsync();

        Assert.Equal(ContactResolveRuntimeUnavailableReason.MalformedConfiguration,
            current.UnavailableReason);
        Assert.Null(current.PrivacyRoutedTransportFactory);
        Assert.Null(current.TrustedAuthorityVerifierFactory);
    }

    [Fact]
    public async Task ClockInstancesPreserveBootScopeAndRejectSameScopeRollback()
    {
        var scope = Bytes(16, 0x71);
        var platform = new MutablePlatformSource(scope, 100);
        var firstProcess = new PlatformOnionMonotonicClock(platform);
        var first = await firstProcess.ReadAsync(CancellationToken.None);
        platform.ElapsedSeconds = 125;
        var restartedProcess = new PlatformOnionMonotonicClock(platform);
        var restarted = await restartedProcess.ReadAsync(CancellationToken.None);

        Assert.Equal(first.BootId.ToArray(), restarted.BootId.ToArray());
        Assert.Equal((ulong)125, restarted.SampleSeconds);

        platform.ElapsedSeconds = 124;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await restartedProcess.ReadAsync(CancellationToken.None));

        platform.ScopeId = Bytes(16, 0x72);
        platform.ElapsedSeconds = 1;
        var rebooted = await restartedProcess.ReadAsync(CancellationToken.None);
        Assert.NotEqual(restarted.BootId.ToArray(), rebooted.BootId.ToArray());
        Assert.Equal((ulong)1, rebooted.SampleSeconds);
    }

    [Fact]
    public void ProductionBoundaryHasNoRawHopKeyOrProofInputs()
    {
        foreach (var type in new[]
                 {
                     typeof(ProductionContactResolveHostOptions),
                     typeof(ProductionContactResolveVerifiedHostCapabilities),
                 })
        {
            var properties = type.GetProperties();
            Assert.DoesNotContain(properties, static property =>
                property.PropertyType == typeof(byte[])
                || property.PropertyType == typeof(ReadOnlyMemory<byte>)
                || property.PropertyType == typeof(Uri)
                || property.PropertyType == typeof(PrivacyMailboxRoute)
                || property.Name.Contains("Hop", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Key", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Proof", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Signer", StringComparison.OrdinalIgnoreCase)
                || property.PropertyType == typeof(bool));
        }
    }

    [Fact]
    public void VerifiedIngressPairAllowsBoundLoopbackCarrierButRejectsPublicHttp()
    {
        Assert.Throws<ArgumentException>(() =>
            new VerifiedContactResolvePrivacyIngressPair(
                NetworkId,
                new Uri("http://primary.example/"),
                new Uri("https://fallback.example/")));

        var routerId = Bytes(32, 0x71);
        var pair = new VerifiedContactResolvePrivacyIngressPair(
            NetworkId,
            new Uri("http://127.0.0.1:17891/"),
            routerId,
            new Uri("http://127.0.0.1:17891/"),
            routerId);

        Assert.Equal(routerId, pair.PrimaryRouterId.ToArray());
        Assert.Equal(routerId, pair.FallbackRouterId.ToArray());
    }

    private static ProductionContactResolveRuntimePrerequisitesSource Source(
        ProductionContactResolveHostOptions? host,
        Func<HttpServiceTransportFactory>? transportFactory = null) =>
        new(
            host,
            () => NetworkId.ToArray(),
            () => new PlatformOnionMonotonicClock(
                new MutablePlatformSource(Bytes(16, 0x41), 5)),
            transportFactory ??
                (() => new HttpServiceTransportFactory(HttpServiceEndpointPolicy.Production)),
            () => null);

    private static ProductionContactResolveHostOptions CompleteHost(
        byte[] networkId,
        Func<PrivacyMailboxRoute?>? primary = null,
        Func<PrivacyMailboxRoute?>? fallback = null) =>
        new(
            () => new XPointNetworkGenesisPin(networkId, Bytes(32, 0x31)),
            TrustedVerifier,
            primary ?? (() => Route(new Uri("https://primary.example/"))),
            fallback ?? (() => Route(new Uri("https://fallback.example/"))),
            Codec,
            () => new NoUsePlacementSource());

    private static PrivacyMailboxRoute Route(Uri origin) =>
        new(origin, new NoUsePathProvider());

    private static PrivacyRoutingCodec Codec() =>
        new(
            new OnionEntropyAuthority(new NoUseEntropyLedger()),
            new OnionKeyAgreementAuthority(new NoUseKeyAgreementVault()));

    private static ContactResolverTrustedVerifier TrustedVerifier() =>
        new(new NoUseVerifiedCapabilitySource());

    private static byte[] Bytes(int length, byte value) =>
        Enumerable.Repeat(value, length).ToArray();

    private sealed class MutablePlatformSource(byte[] scopeId, ulong elapsedSeconds)
        : IPlatformMonotonicSource
    {
        internal byte[] ScopeId { get; set; } = scopeId;
        internal ulong ElapsedSeconds { get; set; } = elapsedSeconds;

        public PlatformMonotonicSample Read() =>
            new(ScopeId.ToArray(), ElapsedSeconds, PlatformMonotonicScope.OperatingSystemBoot);
    }

    private sealed class DelegateHostSource(
        Func<ProductionContactResolveHostOptions?> factory)
        : IProductionContactResolveHostOptionsSource
    {
        public ValueTask<ProductionContactResolveHostOptions?> GetCurrentAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(factory());
        }
    }

    private sealed class NoUsePathProvider : IPrivacyMailboxPathProvider
    {
        public ValueTask<PrivacyMailboxOnionAttempt> PrepareAsync(
            OnionOperation operation,
            ReadOnlyMemory<byte> exactCanonicalRequest,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<PrivacyMailboxOnionAttempt>(
                new InvalidOperationException("The composition test must not prepare a route."));
    }

    private sealed class NoUseEntropyLedger : IOnionEntropyUniquenessLedger
    {
        public ValueTask<OnionEntropyCommitOutcome> CommitAsync(
            OnionEntropyCommitmentBatch batch,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<OnionEntropyCommitOutcome>(
                new InvalidOperationException("The composition test must not reserve entropy."));
    }

    private sealed class NoUseKeyAgreementVault : IOnionKeyAgreementVault
    {
        public ValueTask<byte[]> DeriveX25519SharedSecretAsync(
            OnionKeyHandle keyHandle,
            ReadOnlyMemory<byte> peerPublicKey,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<byte[]>(
                new InvalidOperationException("The composition test must not derive a key."));
    }

    private sealed class NoUseVerifiedCapabilitySource
        : IContactResolverVerifiedCapabilitySource
    {
        public ValueTask<ContactResolverVerifiedCapabilitySet> VerifyAsync(
            ContactResolverVerificationInput input,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ContactResolverVerifiedCapabilitySet>(
                new InvalidOperationException("The composition test must not verify capabilities."));
    }

    private sealed class NoUsePlacementSource : IContactResolvePlacementContextSource
    {
        public ValueTask<VerifiedContactResolverPlacementContext> MintPlacementContextAsync(
            Deep.Client.Shared.Domain.ContactV1.ContactStoreScope accountScope,
            Deep.Client.Shared.Domain.ContactV1.PendingContactAddress pendingAddress,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<VerifiedContactResolverPlacementContext>(
                new InvalidOperationException("The composition test must not mint placement."));
    }

}
