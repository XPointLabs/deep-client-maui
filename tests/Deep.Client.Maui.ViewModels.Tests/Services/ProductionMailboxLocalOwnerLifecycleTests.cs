using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.MailboxTopology;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class ProductionMailboxLocalOwnerLifecycleTests
{
    [Fact]
    public async Task PredecessorRouteState_SurvivesSqliteRestartExactly()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "deep-production-route-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.db");
        var account = SessionId.CreateNew();
        var expected = PublicRoute(Bytes(0x09, 32));
        try
        {
            using (var store = new SqliteSessionStore(path))
                await new ProductionMailboxLocalOwnerRouteStateStore(store)
                    .SaveAsync(account, expected);
            using (var reopened = new SqliteSessionStore(path))
            {
                var actual = await new ProductionMailboxLocalOwnerRouteStateStore(reopened)
                    .LoadAsync(account);
                Assert.NotNull(actual);
                Assert.Equal(expected.CanonicalRouteAdvertisement.ToArray(),
                    actual!.CanonicalRouteAdvertisement.ToArray());
                Assert.Equal(expected.MailboxOwnerEd25519PublicKey.ToArray(),
                    actual.MailboxOwnerEd25519PublicKey.ToArray());
                Assert.Equal(expected.ExpiresAtUnixSeconds, actual.ExpiresAtUnixSeconds);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Valid_ReusesExactVerifiedActivationWithoutNetwork()
    {
        var owner = Bytes(0x11, 32);
        var material = Material();
        var route = PublicRoute(owner);
        var calls = 0;

        var resolved = await ProductionMailboxLocalOwnerLifecycle.ResolveAsync(
            new ProductionMailboxActiveBundleLoadResult(
                ProductionMailboxActiveBundleStatus.Valid, material, route),
            owner,
            _ =>
            {
                calls++;
                throw new InvalidOperationException("Network must not be reached.");
            });

        Assert.Same(material, resolved.Material);
        Assert.Same(route, resolved.PublicRoute);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(ProductionMailboxActiveBundleStatus.Absent)]
    [InlineData(ProductionMailboxActiveBundleStatus.RefreshRecommended)]
    [InlineData(ProductionMailboxActiveBundleStatus.Expired)]
    public async Task RecoverableState_PerformsAuthenticatedAcquireAndActivation(
        ProductionMailboxActiveBundleStatus status)
    {
        var owner = Bytes(0x21, 32);
        var acquired = Acquired(owner);
        var calls = 0;

        var resolved = await ProductionMailboxLocalOwnerLifecycle.ResolveAsync(
            new ProductionMailboxActiveBundleLoadResult(status, null),
            owner,
            _ =>
            {
                calls++;
                return Task.FromResult(acquired);
            });

        Assert.Equal(1, calls);
        Assert.Same(acquired.Material, resolved.Material);
        Assert.Equal(acquired.CanonicalRouteAdvertisement.ToArray(),
            resolved.PublicRoute.CanonicalRouteAdvertisement.ToArray());
        Assert.Equal(owner, resolved.PublicRoute.MailboxOwnerEd25519PublicKey.ToArray());
    }

    [Fact]
    public async Task TransientRefreshFailure_IsRetryableWithoutChangingDecision()
    {
        var owner = Bytes(0x31, 32);
        var acquired = Acquired(owner);
        var calls = 0;
        var active = new ProductionMailboxActiveBundleLoadResult(
            ProductionMailboxActiveBundleStatus.RefreshRecommended,
            Material(),
            PublicRoute(owner));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            ProductionMailboxLocalOwnerLifecycle.ResolveAsync(
                active,
                owner,
                _ =>
                {
                    calls++;
                    throw new TimeoutException("Synthetic transient Registry timeout.");
                }));

        var retried = await ProductionMailboxLocalOwnerLifecycle.ResolveAsync(
            active,
            owner,
            _ =>
            {
                calls++;
                return Task.FromResult(acquired);
            });

        Assert.Equal(2, calls);
        Assert.Same(acquired.Material, retried.Material);
    }

    [Fact]
    public async Task RestartAfterActivation_LoadsExactActiveGenerationWithoutReacquire()
    {
        var owner = Bytes(0x41, 32);
        var acquired = Acquired(owner);
        var activated = await ProductionMailboxLocalOwnerLifecycle.ResolveAsync(
            new ProductionMailboxActiveBundleLoadResult(
                ProductionMailboxActiveBundleStatus.Expired, null),
            owner,
            _ => Task.FromResult(acquired));
        var callsAfterRestart = 0;

        var restarted = await ProductionMailboxLocalOwnerLifecycle.ResolveAsync(
            new ProductionMailboxActiveBundleLoadResult(
                ProductionMailboxActiveBundleStatus.Valid,
                activated.Material,
                activated.PublicRoute),
            owner,
            _ =>
            {
                callsAfterRestart++;
                throw new InvalidOperationException("Exact active replay must not reacquire.");
            });

        Assert.Equal(0, callsAfterRestart);
        Assert.Same(activated.Material, restarted.Material);
        Assert.Same(activated.PublicRoute, restarted.PublicRoute);
    }

    [Fact]
    public async Task CorruptOrConflictingOwner_FailsClosedWithoutActivation()
    {
        var owner = Bytes(0x51, 32);
        var calls = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProductionMailboxLocalOwnerLifecycle.ResolveAsync(
                new ProductionMailboxActiveBundleLoadResult(
                    ProductionMailboxActiveBundleStatus.Corrupt, null),
                owner,
                _ =>
                {
                    calls++;
                    return Task.FromResult(Acquired(owner));
                }));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProductionMailboxLocalOwnerLifecycle.ResolveAsync(
                new ProductionMailboxActiveBundleLoadResult(
                    ProductionMailboxActiveBundleStatus.Valid,
                    Material(),
                    PublicRoute(Bytes(0x52, 32))),
                owner,
                _ => throw new InvalidOperationException()));

        Assert.Equal(0, calls);
    }

    private static AcquiredProductionMailboxLocalOwner Acquired(byte[] owner)
    {
        var certificate = new ProductionMailboxRouteCertificate
        {
            NetworkId = Bytes(0x61, 16),
            AuthorityGeneration = 7,
            CanonicalAuthorityHash = Bytes(0x62, 32),
            IssuerEd25519PublicKey = Bytes(0x63, 32),
            MailboxOwnerEd25519PublicKey = owner,
            BlindedMailboxId = Bytes(0x64, 32),
            BlindedPlacementId = Bytes(0x65, 32),
            SelectionInputCommitment = Bytes(0x66, 32),
            IssuedAtUnixSeconds = 1_000,
            ExpiresAtUnixSeconds = 2_000,
            IssuerSignature = Bytes(0x67, 64)
        };
        var advertisement = new ProductionMailboxRouteAdvertisement
        {
            Certificate = certificate,
            Sequence = 9,
            PublishedAtUnixSeconds = 1_000,
            ExpiresAtUnixSeconds = 1_900,
            OwnerSignature = Bytes(0x68, 64)
        };
        return new AcquiredProductionMailboxLocalOwner(
            Material(),
            ProductionMailboxRouteAdvertisementCodec.EncodeAdvertisement(advertisement));
    }

    private static ProductionMailboxLocalOwnerPublicRoute PublicRoute(byte[] owner) => new(
        owner,
        Acquired(owner).CanonicalRouteAdvertisement,
        1_900);

    private static ImportedProductionMailboxRuntimeMaterial Material()
    {
        var account = SessionId.CreateNew();
        var selector = new MailboxCredentialSelector(
            OutboxAccountScope.FromBytes(Bytes(0x71, 32)),
            MailboxCredentialScopeKind.Self,
            Bytes(0x72, 32),
            Bytes(0x73, 32));
        return new ImportedProductionMailboxRuntimeMaterial(
            null!,
            null!,
            null!,
            selector,
            account,
            MailboxInfrastructureOwnership.OfficialManaged);
    }

    private static byte[] Bytes(byte value, int count) =>
        Enumerable.Repeat(value, count).ToArray();
}
