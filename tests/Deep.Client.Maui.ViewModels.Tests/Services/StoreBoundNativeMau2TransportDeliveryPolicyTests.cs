using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.MailboxCapabilities;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class StoreBoundNativeMau2TransportDeliveryPolicyTests
{
    private const string RecoveryPhrase =
        "amaze buffet cake entrance symptoms tiger lamb maze nestle python dusted faxed faxed";

    [Theory]
    [InlineData(MailboxDeliveryKind.Direct)]
    [InlineData(MailboxDeliveryKind.GroupState)]
    [InlineData(MailboxDeliveryKind.GroupMessage)]
    public async Task DecideUsesVerifiedRecipientSelectorForSupportedKinds(
        MailboxDeliveryKind kind)
    {
        using var fixture = Fixture.Create();
        await fixture.BindAsync();

        var decision = await fixture.Transport.DecideAsync(
            Request(fixture.Identity.SessionId, fixture.Recipient, kind));

        Assert.Equal(MailboxTransportProtocol.AuthenticatedMau2, decision.Protocol);
        Assert.Equal(MailboxInfrastructureOwnership.UserManaged, decision.Ownership);
        Assert.Same(fixture.Runtime.Authority, decision.Authority);
        Assert.Same(fixture.RecipientSelector, decision.Selector);
        Assert.Equal(1, fixture.ProvisioningSource.Calls);
    }

    [Fact]
    public async Task DecideRejectsUnknownKindBeforeProvisioning()
    {
        using var fixture = Fixture.Create();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            fixture.Transport.DecideAsync(Request(
                fixture.Identity.SessionId,
                fixture.Recipient,
                (MailboxDeliveryKind)int.MaxValue)));

        Assert.Equal(0, fixture.ProvisioningSource.Calls);
    }

    [Theory]
    [InlineData(MailboxDeliveryKind.Direct)]
    [InlineData(MailboxDeliveryKind.GroupState)]
    [InlineData(MailboxDeliveryKind.GroupMessage)]
    public async Task DecideRejectsRecipientWithoutVerifiedSelector(
        MailboxDeliveryKind kind)
    {
        using var fixture = Fixture.Create();
        await fixture.BindAsync();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            fixture.Transport.DecideAsync(Request(
                fixture.Identity.SessionId,
                SessionId.CreateNew(),
                kind)));

        Assert.Equal(1, fixture.ProvisioningSource.Calls);
    }

    private static MailboxDeliveryRequest Request(
        SessionId sender,
        SessionId recipient,
        MailboxDeliveryKind kind) => new(
        new OutboundMessageEnvelope(
            sender,
            recipient,
            "opaque",
            [],
            DateTimeOffset.FromUnixTimeSeconds(1_050),
            DateTimeOffset.FromUnixTimeSeconds(1_100),
            new MessageId($"delivery-policy-{Guid.NewGuid():N}")),
        kind);

    private static byte[] Bytes(int count, byte value) =>
        Enumerable.Repeat(value, count).ToArray();

    private sealed class Fixture : IDisposable
    {
        private readonly string directory;
        private readonly SqliteSessionStore sqlite;
        private readonly SecureRecoverySessionStore secureStore;

        private Fixture(
            string directory,
            SqliteSessionStore sqlite,
            SecureRecoverySessionStore secureStore,
            SessionIdentityProvider identity,
            SessionId recipient,
            MailboxCredentialSelector recipientSelector,
            ProvisionedMailboxRuntime runtime,
            TestProvisioningSource provisioningSource,
            StoreBoundNativeMau2Transport transport)
        {
            this.directory = directory;
            this.sqlite = sqlite;
            this.secureStore = secureStore;
            Identity = identity;
            Recipient = recipient;
            RecipientSelector = recipientSelector;
            Runtime = runtime;
            ProvisioningSource = provisioningSource;
            Transport = transport;
        }

        public SessionIdentityProvider Identity { get; }
        public SessionId Recipient { get; }
        public MailboxCredentialSelector RecipientSelector { get; }
        public ProvisionedMailboxRuntime Runtime { get; }
        public TestProvisioningSource ProvisioningSource { get; }
        public StoreBoundNativeMau2Transport Transport { get; }

        public static Fixture Create()
        {
            var directory = Path.Combine(
                Path.GetTempPath(), $"deep-mau2-delivery-policy-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var sqlite = new SqliteSessionStore(new SqliteSessionStoreOptions(
                Path.Combine(directory, "client-state.db"),
                new string('A', 64)));
            var secureStore = new SecureRecoverySessionStore(sqlite);
            var identity = new SessionIdentityProvider(RecoveryPhrase);
            try
            {
                var recipient = SessionId.CreateNew();
                var issuerContext = Bytes(32, 0x31);
                var clock = new FixedTimeProvider(
                    DateTimeOffset.FromUnixTimeSeconds(1_050));
                var accountScope = OutboxAccountScope.FromBytes(Bytes(32, 0x41));
                var recipientSelector = new MailboxCredentialSelector(
                    accountScope,
                    MailboxCredentialScopeKind.Peer,
                    Bytes(32, 0x42),
                    issuerContext);
                var selfSelector = new MailboxCredentialSelector(
                    accountScope,
                    MailboxCredentialScopeKind.Self,
                    Bytes(32, 0x43),
                    issuerContext);
                var authority = new VerifiedOfficialMailboxAuthority(
                    Bytes(16, 0x21),
                    1,
                    [Issuer(clock)],
                    requiresManagedEntitlement: false,
                    static () => false,
                    new EmptyRevocations(),
                    clock,
                    MailboxRuntimePolicyCoordinator.For(
                        sqlite.CanonicalStateIdentity,
                        issuerContext));
                var runtime = new ProvisionedMailboxRuntime(
                    authority,
                    new ClientMailboxActivation(
                        enabled: true,
                        issuerContext,
                        ingressConfigured: true),
                    Policies(clock),
                    identity.SessionId,
                    selfSelector,
                    (sessionId, _) => Task.FromResult<MailboxCredentialSelector?>(
                        sessionId == identity.SessionId
                            ? selfSelector
                            : sessionId == recipient
                                ? recipientSelector
                                : null),
                    new RejectingIngress(),
                    clock);
                var provisioningSource = new TestProvisioningSource(runtime);
                var transport = new StoreBoundNativeMau2Transport(
                    sqlite,
                    secureStore,
                    provisioningSource,
                    MailboxInfrastructureOwnership.UserManaged,
                    ClientFeatureFlags.ReleaseDefaults with
                    {
                        ClientMailboxAdapterEnabled = true
                    });
                return new Fixture(
                    directory,
                    sqlite,
                    secureStore,
                    identity,
                    recipient,
                    recipientSelector,
                    runtime,
                    provisioningSource,
                    transport);
            }
            catch
            {
                identity.Dispose();
                secureStore.Dispose();
                sqlite.Dispose();
                TryDeleteDirectory(directory);
                throw;
            }
        }

        public async Task BindAsync()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Transport.ReceiveAuthenticatedAsync(Identity));
            Assert.Equal(1, ProvisioningSource.Calls);
        }

        public void Dispose()
        {
            Transport.Dispose();
            Identity.Dispose();
            secureStore.Dispose();
            sqlite.Dispose();
            TryDeleteDirectory(directory);
        }

        private static MailboxCapabilityIssuerAuthority Issuer(TimeProvider clock)
        {
            var now = checked((ulong)clock.GetUtcNow().ToUnixTimeSeconds());
            return new MailboxCapabilityIssuerAuthority
            {
                PublicKey = Bytes(32, 0x51),
                Domain = MailboxCapabilityDomain.Deposit,
                AllowedLifecycle = MailboxCapabilityLifecycle.Active,
                MinimumGeneration = 1,
                MaximumGeneration = 2,
                ValidFromUnixSeconds = now - 60,
                ValidUntilUnixSeconds = now + 3_600
            };
        }

        private static IMailboxClientDecodePolicyProvider Policies(TimeProvider clock) =>
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
                clock);

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
    }

    private sealed class TestProvisioningSource(ProvisionedMailboxRuntime runtime) :
        IMailboxRuntimeProvisioningSource
    {
        public int Calls { get; private set; }

        public Task<ProvisionedMailboxRuntime> ProvisionAsync(
            SqliteSessionStore store,
            MailboxHolderIdentity holder,
            MailboxInfrastructureOwnership ownership,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Assert.Equal(runtime.LocalSessionId, holder.SessionId);
            Assert.Equal(MailboxInfrastructureOwnership.UserManaged, ownership);
            return Task.FromResult(runtime);
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
