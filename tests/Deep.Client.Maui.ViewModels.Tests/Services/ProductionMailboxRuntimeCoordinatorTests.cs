using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.MailboxCapabilities;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

[Collection("SQLite global pool isolation")]
public sealed class ProductionMailboxRuntimeCoordinatorTests
{
    private const string AccountARecoveryPhrase =
        "amaze buffet cake entrance symptoms tiger lamb maze nestle python dusted faxed faxed";
    private const string AccountBRecoveryPhrase =
        "update vague zinger boxes ornament renting glass gained island nabbing afield calamity nabbing";

    [Fact]
    public void ProductionCoordinatorExplicitlyAdvertisesReactiveRejectedRetrieveRefresh()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        try
        {
            using var coordinator = new ProductionMailboxRuntimeCoordinator(
                () => new Uri("https://registry.example.net/"),
                root,
                new HttpServiceTransportFactory(HttpServiceEndpointPolicy.Production),
                new HttpServiceClientOptions());

            Assert.True(((IMailboxRuntimeProvisioningSource)coordinator)
                .SupportsReactiveRejectedRetrieveRefresh);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void ProductionCoordinatorDefersRegistryConfigurationUntilNetworkProvisioning()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        var originResolutions = 0;
        try
        {
            using (var coordinator = new ProductionMailboxRuntimeCoordinator(
                       () =>
                       {
                           originResolutions++;
                           throw new InvalidOperationException(
                               "Registry configuration is intentionally unavailable.");
                       },
                       root,
                       new HttpServiceTransportFactory(HttpServiceEndpointPolicy.Production),
                       new HttpServiceClientOptions()))
            {
                Assert.True(((IMailboxRuntimeProvisioningSource)coordinator)
                    .SupportsReactiveRejectedRetrieveRefresh);
                Assert.Equal(0, originResolutions);
            }

            Assert.Equal(0, originResolutions);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ProductionProvisionFailsBeforeRegistryOrNetworkDispatch()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        var originResolutions = 0;
        try
        {
            using var sqlite = new SqliteSessionStore(Path.Combine(root, "state.db"));
            using var coordinator = new ProductionMailboxRuntimeCoordinator(
                () =>
                {
                    originResolutions++;
                    return new Uri("https://registry.example.net/");
                },
                root,
                new HttpServiceTransportFactory(HttpServiceEndpointPolicy.Production),
                new HttpServiceClientOptions());
            var holder = new MailboxHolderIdentity(
                SessionId.CreateNew(),
                Bytes(32, 0x41));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinator.ProvisionAsync(
                    sqlite,
                    holder,
                    MailboxInfrastructureOwnership.OfficialManaged));

            Assert.Equal(
                ProductionMailboxPrivacyRouteBootstrap.UnavailableCode,
                exception.Message);
            Assert.Equal(0, originResolutions);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PersistedPeerSelectorSurvivesStoreRestartWithoutNetworkAcquisition()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.db");
        var local = SessionId.CreateNew();
        var peer = SessionId.CreateNew();
        var selector = new MailboxCredentialSelector(
            OutboxAccountScope.FromBytes(Bytes(32, 0x21)),
            MailboxCredentialScopeKind.Peer,
            Bytes(32, 0x31),
            Bytes(32, 0x41));
        try
        {
            using (var sqlite = new SqliteSessionStore(path))
            {
                await new PersistedMailboxPeerSelectorStore(sqlite)
                    .SaveAsync(local, peer, selector);
            }

            using (var reopened = new SqliteSessionStore(path))
            {
                var restored = await new PersistedMailboxPeerSelectorStore(reopened)
                    .LoadAsync(local, peer);

                Assert.NotNull(restored);
                Assert.Equal(MailboxCredentialScopeKind.Peer, restored!.Kind);
                Assert.Equal(selector.AccountScope.ToArray(),
                    restored.AccountScope.ToArray());
                Assert.Equal(selector.SubjectId.ToArray(), restored.SubjectId.ToArray());
                Assert.Equal(selector.IssuerContext.ToArray(), restored.IssuerContext.ToArray());
                Assert.Equal(selector.ScopeId.ToArray(), restored.ScopeId.ToArray());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task StopReleasesCoordinatorBindingAndAllowsAnotherAccountGeneration()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        try
        {
            using var sqlite = new SqliteSessionStore(new SqliteSessionStoreOptions(
                Path.Combine(root, "state.db"), new string('A', 64)));
            using var secureStore = new SecureRecoverySessionStore(sqlite);
            using var accountA = new SessionIdentityProvider(AccountARecoveryPhrase);
            using var accountB = new SessionIdentityProvider(AccountBRecoveryPhrase);
            var source = new LifecycleProvisioningSource();
            using var transport = new StoreBoundNativeMau2Transport(
                sqlite,
                secureStore,
                source,
                MailboxInfrastructureOwnership.OfficialManaged,
                ClientFeatureFlags.ReleaseDefaults with
                {
                    ClientMailboxAdapterEnabled = true
                });

            Assert.Same(sqlite, source.AttachedStore);
            Assert.Same(secureStore, source.AttachedSecureStore);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => transport.ReceiveAuthenticatedAsync(accountA));

            await transport.StopAsync(accountA.SessionId);
            Assert.Equal([accountA.SessionId], source.ReleasedAccounts);
            transport.Resume(accountB.SessionId);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => transport.ReceiveAuthenticatedAsync(accountB));
            Assert.Equal([accountA.SessionId, accountB.SessionId], source.ProvisionedAccounts);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteDirectory(root);
        }
    }

    private static byte[] Bytes(int count, byte value) =>
        Enumerable.Repeat(value, count).ToArray();

    private static string Root() => Path.Combine(
        Path.GetTempPath(), "deep-production-runtime-coordinator-" + Guid.NewGuid().ToString("N"));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class LifecycleProvisioningSource : IMailboxRuntimeProvisioningSource
    {
        public SqliteSessionStore? AttachedStore { get; private set; }
        public SecureRecoverySessionStore? AttachedSecureStore { get; private set; }
        public List<SessionId> ProvisionedAccounts { get; } = [];
        public List<SessionId> ReleasedAccounts { get; } = [];

        public void AttachRuntimeState(
            SqliteSessionStore store,
            SecureRecoverySessionStore secureStore)
        {
            AttachedStore = store;
            AttachedSecureStore = secureStore;
        }

        public Task<ProvisionedMailboxRuntime> ProvisionAsync(
            SqliteSessionStore store,
            MailboxHolderIdentity holder,
            MailboxInfrastructureOwnership ownership,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Same(AttachedStore, store);
            Assert.Equal(MailboxInfrastructureOwnership.OfficialManaged, ownership);
            ProvisionedAccounts.Add(holder.SessionId);
            return Task.FromResult(Runtime(store, holder.SessionId));
        }

        public Task ReleaseAsync(
            SessionId account,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReleasedAccounts.Add(account);
            return Task.CompletedTask;
        }

        private static ProvisionedMailboxRuntime Runtime(
            SqliteSessionStore store,
            SessionId account)
        {
            var clock = new FixedTimeProvider(
                DateTimeOffset.FromUnixTimeSeconds(1_050));
            var issuerContext = Bytes(32, 0x51);
            var accountScope = OutboxAccountScope.FromBytes(Bytes(32, 0x61));
            var self = new MailboxCredentialSelector(
                accountScope,
                MailboxCredentialScopeKind.Self,
                Bytes(32, 0x71),
                issuerContext);
            var authority = new VerifiedOfficialMailboxAuthority(
                Bytes(16, 0x11),
                1,
                [new MailboxCapabilityIssuerAuthority
                {
                    PublicKey = Bytes(32, 0x12),
                    Domain = MailboxCapabilityDomain.Deposit,
                    AllowedLifecycle = MailboxCapabilityLifecycle.Active,
                    MinimumGeneration = 1,
                    MaximumGeneration = 2,
                    ValidFromUnixSeconds = 900,
                    ValidUntilUnixSeconds = 2_000
                }],
                requiresManagedEntitlement: false,
                static () => false,
                new EmptyRevocations(),
                clock,
                MailboxRuntimePolicyCoordinator.For(
                    store.CanonicalStateIdentity, issuerContext));
            return new ProvisionedMailboxRuntime(
                authority,
                new ClientMailboxActivation(true, issuerContext, true),
                new TimeProviderMailboxClientDecodePolicyProvider(
                    new MailboxEpochWindow
                    {
                        CurrentEpoch = 7,
                        NextEpoch = 8,
                        CurrentNotBeforeUnixSeconds = 900,
                        NextNotBeforeUnixSeconds = 1_100,
                        CurrentExpiresAtUnixSeconds = 1_120,
                        NextExpiresAtUnixSeconds = 1_200
                    },
                    new MailboxCapabilityDecodePolicy
                    {
                        CurrentBucket = 1_050,
                        MinimumGeneration = 1
                    },
                    clock),
                account,
                self,
                (recipient, _) => Task.FromResult<MailboxCredentialSelector?>(
                    recipient == account ? self : null),
                new RejectingIngress(),
                clock);
        }
    }

    private sealed class EmptyRevocations : IFreshMailboxCapabilityRevocationSource
    {
        public void ValidateFreshness()
        {
        }

        public bool IsRevoked(MailboxCapabilityRevocationQuery query) => false;
    }

    private sealed class RejectingIngress : IClientMailboxBinaryIngress, IDisposable
    {
        public Task<ReadOnlyMemory<byte>> StoreAsync(
            ReadOnlyMemory<byte> canonicalMau2,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ReadOnlyMemory<byte>>(new NotSupportedException());

        public Task<ReadOnlyMemory<byte>> RetrieveAsync(
            ReadOnlyMemory<byte> canonicalMau2,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ReadOnlyMemory<byte>>(new NotSupportedException());

        public Task<ReadOnlyMemory<byte>> AcknowledgeAsync(
            ReadOnlyMemory<byte> canonicalMau2,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ReadOnlyMemory<byte>>(new NotSupportedException());

        public void Dispose()
        {
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
