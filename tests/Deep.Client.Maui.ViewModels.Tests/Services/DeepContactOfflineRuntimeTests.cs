using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Domain.ContactV1;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Persistence.AccountDirectoryV1;
using Deep.Client.Shared.Persistence.ContactV1;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.ContactV1;
using Deep.Client.Shared.Services.XPointNetworkV1;
using Deep.Protocol.ApplicationCore;
using Deep.Protocol.ContactV1;
using Deep.Protocol.DeepExtension.PrivacyRouting;
using Deep.Protocol.XPointNetworkV1;
using Sodium;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class DeepContactOfflineRuntimeTests
{
    private static readonly byte[] NetworkId = Enumerable.Range(1, 16)
        .Select(static value => (byte)value).ToArray();

    [Fact]
    public async Task PermanentIdAndCanonicalPendingImportsSurviveOfflineRestart()
    {
        using var fixture = new RuntimeFixture();
        string permanentId;
        string did;
        string dia;
        DateTimeOffset didImportedAt;
        DateTimeOffset diaImportedAt;

        await using (var first = fixture.CreateAccessor())
        {
            var created = await CreateAccountAsync(await first.GetAccountsAsync());
            permanentId = created.Identity.Account.PermanentId.CanonicalText;
            Assert.Equal(permanentId,
                await first.GetPermanentDeepIdAsync());

            did = ApplicationCoreCodec.AuthorDid1(Bytes(32, 0x41), Bytes(16, 0x42)).Text;
            dia = CanonicalDia(NetworkId, 2_000_000_000);
            var importer = await first.GetContactImporterAsync();
            var didResult = await importer.ImportAsync(did);
            var diaResult = await importer.ImportAsync(dia);
            didImportedAt = didResult.PendingAddress.ImportedAt;
            diaImportedAt = diaResult.PendingAddress.ImportedAt;

            Assert.Equal(PendingContactAddressWriteDisposition.Added, didResult.Disposition);
            Assert.Equal(PendingContactAddressWriteDisposition.Added, diaResult.Disposition);
            Assert.Equal(ContactRelationshipState.Absent, didResult.PendingAddress.RelationshipState);
            Assert.Equal(ContactRelationshipState.Absent, diaResult.PendingAddress.RelationshipState);
            Assert.Null(didResult.PendingAddress.RelationshipId);
            Assert.Null(diaResult.PendingAddress.ConversationId);
        }

        await using (var reopened = fixture.CreateAccessor())
        {
            Assert.Equal(permanentId, await reopened.GetPermanentDeepIdAsync());
            var importer = await reopened.GetContactImporterAsync();
            var didReplay = await importer.ImportAsync(did);
            var diaReplay = await importer.ImportAsync(dia);

            Assert.Equal(PendingContactAddressWriteDisposition.Idempotent, didReplay.Disposition);
            Assert.Equal(PendingContactAddressWriteDisposition.Idempotent, diaReplay.Disposition);
            Assert.Equal(didImportedAt, didReplay.PendingAddress.ImportedAt);
            Assert.Equal(diaImportedAt, diaReplay.PendingAddress.ImportedAt);
        }

        Assert.Equal(2, fixture.NetworkIdReads);
        Assert.Equal(2, fixture.SecureStorageCreates);
    }

    [Fact]
    public async Task NonCanonicalInputRejectsWithoutCreatingConversationOrCallingNetworkRuntime()
    {
        using var fixture = new RuntimeFixture();
        await using var accessor = fixture.CreateAccessor();
        await CreateAccountAsync(await accessor.GetAccountsAsync());
        var importer = await accessor.GetContactImporterAsync();
        var canonical = ApplicationCoreCodec.AuthorDid1(Bytes(32, 0x51), Bytes(16, 0x52)).Text;

        var failure = await Assert.ThrowsAsync<ContactAddressImportException>(() =>
            importer.ImportAsync(" " + canonical).AsTask());

        Assert.Equal(ContactAddressImportFailure.NonCanonicalOrUnsupported, failure.Failure);
        Assert.Equal(1, fixture.NetworkIdReads);
        Assert.Equal(1, fixture.SecureStorageCreates);
    }

    [Fact]
    public async Task ResetClosesContactStoreAndPurgesDatabaseFamilyAndGenerationKeySlot()
    {
        using var fixture = new RuntimeFixture();
        await using var accessor = fixture.CreateAccessor();
        var accounts = await accessor.GetAccountsAsync();
        var created = await CreateAccountAsync(accounts);
        var instanceSlot = created.Identity.SecureSlots.MessageStoreInstanceId;
        var contactSlot = ContactKeySlot(instanceSlot);
        var importer = await accessor.GetContactImporterAsync();
        var did = ApplicationCoreCodec.AuthorDid1(Bytes(32, 0x61), Bytes(16, 0x62)).Text;
        await importer.ImportAsync(did);
        Assert.True(File.Exists(fixture.ContactStatePath));
        using (var storage = fixture.OpenStorage())
        using (var key = await storage.ReadOwnedAsync(contactSlot))
            Assert.NotNull(key);

        await accessor.ResetLocalStateAsync();

        Assert.DoesNotContain(ContactDatabaseFamily(fixture.ContactStatePath), File.Exists);
        using (var storage = fixture.OpenStorage())
        using (var removedKey = await storage.ReadOwnedAsync(contactSlot))
            Assert.Null(removedKey);
        var resetAccounts = await accessor.GetAccountsAsync();
        Assert.Null(await resetAccounts.GetLocalIdentityAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            accessor.GetContactImporterAsync());

        var replacement = await CreateAccountAsync(resetAccounts);
        var replacementContactSlot = ContactKeySlot(
            replacement.Identity.SecureSlots.MessageStoreInstanceId);
        Assert.NotEqual(contactSlot, replacementContactSlot);
        _ = await accessor.GetContactImporterAsync();
        using (var storage = fixture.OpenStorage())
        {
            using var oldKey = await storage.ReadOwnedAsync(contactSlot);
            using var replacementKey = await storage.ReadOwnedAsync(replacementContactSlot);
            Assert.Null(oldKey);
            Assert.NotNull(replacementKey);
        }
    }

    [Fact]
    public void OfflineAccessorCompositionHasNoBootstrapResolverOrTransportDependency()
    {
        var constructor = Assert.Single(typeof(DeepAccountRuntimeAccessor)
            .GetConstructors(System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic));
        var parameterNames = constructor.GetParameters()
            .Select(static parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)
            .ToArray();

        Assert.DoesNotContain(parameterNames, static name =>
            name.Contains("Bootstrap", StringComparison.Ordinal)
            || name.Contains("Resolver", StringComparison.Ordinal)
            || name.Contains("Transport", StringComparison.Ordinal)
            || name.Contains("HttpClient", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AccountDirectoryPersistenceIsAccountScopedEncryptedAndSurvivesRestart()
    {
        using var fixture = new RuntimeFixture();
        string directoryKeySlot;

        await using (var first = fixture.CreateAccessor())
        {
            var created = await CreateAccountAsync(await first.GetAccountsAsync());
            directoryKeySlot = ScopedSlot(
                created.Identity.SecureSlots.MessageStoreInstanceId,
                ".account-directory-state-key");
            var binding = await first.GetContactResolvePersistenceBindingAsync();

            Assert.Null(await binding.AccountDirectoryStore.ReadAsync(CancellationToken.None));
            Assert.Equal(
                created.Identity.Account.AccountIdentity.AccountId,
                binding.ContactStore.Scope.AccountId);
            Assert.True(File.Exists(fixture.AccountDirectoryStatePath));
            using var storage = fixture.OpenStorage();
            using var key = await storage.ReadOwnedAsync(directoryKeySlot);
            Assert.NotNull(key);
        }

        Assert.False(File.ReadAllBytes(fixture.AccountDirectoryStatePath)
            .AsSpan().StartsWith("SQLite format 3\0"u8));

        await using (var reopened = fixture.CreateAccessor())
        {
            var binding = await reopened.GetContactResolvePersistenceBindingAsync();
            Assert.Null(await binding.AccountDirectoryStore.ReadAsync(CancellationToken.None));
            await reopened.ResetLocalStateAsync();
        }

        Assert.DoesNotContain(
            ContactDatabaseFamily(fixture.AccountDirectoryStatePath),
            File.Exists);
        using (var storage = fixture.OpenStorage())
        using (var key = await storage.ReadOwnedAsync(directoryKeySlot))
            Assert.Null(key);
    }

    [Theory]
    [InlineData((int)ContactResolveRuntimeUnavailableReason.GenesisPin,
        ContactResolvePendingStatus.GenesisPinUnavailable)]
    [InlineData((int)ContactResolveRuntimeUnavailableReason.MonotonicClock,
        ContactResolvePendingStatus.MonotonicClockUnavailable)]
    [InlineData((int)ContactResolveRuntimeUnavailableReason.PrivacyRoute,
        ContactResolvePendingStatus.PrivacyRouteUnavailable)]
    [InlineData((int)ContactResolveRuntimeUnavailableReason.AuthoritySource,
        ContactResolvePendingStatus.AuthoritySourceUnavailable)]
    [InlineData((int)ContactResolveRuntimeUnavailableReason.DirectorySource,
        ContactResolvePendingStatus.DirectorySourceUnavailable)]
    [InlineData((int)ContactResolveRuntimeUnavailableReason.MalformedConfiguration,
        ContactResolvePendingStatus.MalformedConfiguration)]
    [InlineData((int)ContactResolveRuntimeUnavailableReason.CrossNetworkConfiguration,
        ContactResolvePendingStatus.CrossNetworkConfiguration)]
    public async Task OfflineImportPersistsRetryablePendingForExactUnavailableAuthority(
        int reasonValue,
        string expectedStatus)
    {
        var reason = (ContactResolveRuntimeUnavailableReason)reasonValue;
        using var fixture = new RuntimeFixture();
        await using var accountRuntime = fixture.CreateAccessor();
        await CreateAccountAsync(await accountRuntime.GetAccountsAsync());
        var source = new FixedPrerequisitesSource(PrerequisitesMissing(reason));
        var runtime = new DeepContactResolveRuntimeAccessor(accountRuntime, source);
        var did = ApplicationCoreCodec.AuthorDid1(Bytes(32, 0x31), Bytes(16, 0x32)).Text;

        var result = await runtime.ImportAndEnqueueResolveAsync(did);

        Assert.Equal(PendingContactAddressWriteDisposition.Added, result.ImportDisposition);
        Assert.Equal(ContactResolveQueueState.PendingRetry, result.QueueState);
        Assert.Equal(ContactResolvePendingStatus.Title, result.Title);
        Assert.Equal(expectedStatus, result.Message);
        Assert.True(result.CanRetry);
        Assert.Equal(1, source.Calls);
        var importer = await accountRuntime.GetContactImporterAsync();
        var replay = await importer.ImportAsync(did);
        Assert.Equal(PendingContactAddressWriteDisposition.Idempotent, replay.Disposition);
    }

    [Fact]
    public async Task PendingResolveReevaluatesCapabilitiesAcrossProcessRestart()
    {
        using var fixture = new RuntimeFixture();
        var did = ApplicationCoreCodec.AuthorDid1(Bytes(32, 0x35), Bytes(16, 0x36)).Text;

        await using (var firstAccountRuntime = fixture.CreateAccessor())
        {
            await CreateAccountAsync(await firstAccountRuntime.GetAccountsAsync());
            var firstSource = new FixedPrerequisitesSource(
                PrerequisitesMissing(ContactResolveRuntimeUnavailableReason.GenesisPin));
            var first = new DeepContactResolveRuntimeAccessor(
                firstAccountRuntime,
                firstSource);

            var queued = await first.ImportAndEnqueueResolveAsync(did);

            Assert.Equal(PendingContactAddressWriteDisposition.Added,
                queued.ImportDisposition);
            Assert.Equal(ContactResolvePendingStatus.GenesisPinUnavailable,
                queued.Message);
            Assert.Equal(1, firstSource.Calls);
        }

        await using (var reopenedAccountRuntime = fixture.CreateAccessor())
        {
            var secondSource = new FixedPrerequisitesSource(
                PrerequisitesMissing(ContactResolveRuntimeUnavailableReason.MonotonicClock));
            var reopened = new DeepContactResolveRuntimeAccessor(
                reopenedAccountRuntime,
                secondSource);

            var retried = await reopened.ImportAndEnqueueResolveAsync(did);

            Assert.Equal(PendingContactAddressWriteDisposition.Idempotent,
                retried.ImportDisposition);
            Assert.Equal(ContactResolvePendingStatus.MonotonicClockUnavailable,
                retried.Message);
            Assert.Equal(1, secondSource.Calls);
        }
    }

    [Fact]
    public async Task AccountStartupDoesNotResolveContactNetworkPrerequisites()
    {
        using var fixture = new RuntimeFixture();
        await using var accountRuntime = fixture.CreateAccessor();
        var source = new FixedPrerequisitesSource(
            PrerequisitesMissing(ContactResolveRuntimeUnavailableReason.GenesisPin));
        _ = new DeepContactResolveRuntimeAccessor(accountRuntime, source);

        await CreateAccountAsync(await accountRuntime.GetAccountsAsync());

        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task NullVerifiedHostCapabilitiesRemainDormantWithoutOpeningAccountState()
    {
        using var fixture = new RuntimeFixture();
        await using var accountRuntime = fixture.CreateAccessor();
        var activeNetworkReads = 0;
        var bootstrap = new ProductionMailboxPrivacyRouteBootstrap(
            accountRuntime,
            capabilities: null,
            () =>
            {
                activeNetworkReads++;
                throw new InvalidOperationException("dormant bootstrap must remain offline");
            });

        var host = await bootstrap.GetCurrentAsync(CancellationToken.None);

        Assert.Null(host);
        Assert.Equal(0, activeNetworkReads);
        Assert.Equal(0, fixture.NetworkIdReads);
        Assert.Equal(0, fixture.SecureStorageCreates);
        Assert.False(File.Exists(fixture.EntropyStatePath));
    }

    [Fact]
    public async Task CompleteVerifiedHostCapabilitiesBindDistinctRoutesToCurrentAccount()
    {
        using var fixture = new RuntimeFixture();
        await using var accountRuntime = fixture.CreateAccessor();
        await CreateAccountAsync(await accountRuntime.GetAccountsAsync());
        var templatePrimary = new PrivacyMailboxRoute(
            new Uri("https://primary.example/"),
            new NoUsePathProvider());
        var templateFallback = new PrivacyMailboxRoute(
            new Uri("https://fallback.example/"),
            new NoUsePathProvider());
        var capabilities = new ProductionContactResolveVerifiedHostCapabilities(
            new XPointNetworkGenesisPin(NetworkId, Bytes(32, 0x92)),
            new NoUseVerifiedCapabilitySource(),
            new NoUsePathAuthoritySource(),
            new FixedVerifiedIngressSource(
                new VerifiedContactResolvePrivacyIngressPair(
                    NetworkId,
                    templatePrimary.EntryOrigin,
                    templateFallback.EntryOrigin)));
        var bootstrap = new ProductionMailboxPrivacyRouteBootstrap(
            accountRuntime,
            capabilities,
            () => NetworkId.ToArray());

        var host = await bootstrap.GetCurrentAsync(CancellationToken.None);
        var cached = await bootstrap.GetCurrentAsync(CancellationToken.None);

        Assert.NotNull(host);
        Assert.NotSame(host, cached);
        Assert.Equal(NetworkId, host!.GenesisPinFactory()!.NetworkId.ToArray());
        var primary = host.PrimaryPrivacyMailboxRouteFactory();
        var fallback = host.FallbackPrivacyMailboxRouteFactory();
        Assert.NotNull(primary);
        Assert.NotNull(fallback);
        Assert.Equal(templatePrimary.EntryOrigin, primary!.EntryOrigin);
        Assert.Equal(templateFallback.EntryOrigin, fallback!.EntryOrigin);
        Assert.IsType<ContactResolvePrivacyPathProvider>(primary.PathProvider);
        Assert.Same(primary.PathProvider, fallback.PathProvider);
        Assert.NotNull(host.PrivacyRoutingCodecFactory());
        Assert.NotNull(host.TrustedXis1VerifierFactory());
        Assert.True(File.Exists(fixture.EntropyStatePath));
    }

    [Fact]
    public async Task CrossNetworkVerifiedHostFailsBeforeAccountOrProtectedStateOpen()
    {
        using var fixture = new RuntimeFixture();
        await using var accountRuntime = fixture.CreateAccessor();
        var capabilities = new ProductionContactResolveVerifiedHostCapabilities(
            new XPointNetworkGenesisPin(Bytes(16, 0xE1), Bytes(32, 0xE2)),
            new NoUseVerifiedCapabilitySource(),
            new NoUsePathAuthoritySource(),
            new FixedVerifiedIngressSource(
                new VerifiedContactResolvePrivacyIngressPair(
                    Bytes(16, 0xE1),
                    new Uri("https://primary.example/"),
                    new Uri("https://fallback.example/"))));
        var bootstrap = new ProductionMailboxPrivacyRouteBootstrap(
            accountRuntime,
            capabilities,
            () => NetworkId.ToArray());
        var source = new ProductionContactResolveRuntimePrerequisitesSource(
            bootstrap,
            () => NetworkId.ToArray(),
            () => new FixedMonotonicClock(),
            () => throw new InvalidOperationException("must remain lazy"),
            () => throw new InvalidOperationException("must remain lazy"));

        var current = await source.GetCurrentAsync();

        Assert.Equal(ContactResolveRuntimeUnavailableReason.CrossNetworkConfiguration,
            current.UnavailableReason);
        Assert.Equal(0, fixture.NetworkIdReads);
        Assert.Equal(0, fixture.SecureStorageCreates);
        Assert.False(File.Exists(fixture.EntropyStatePath));
    }

    [Fact]
    public async Task EntropyLedgerRejectsReplayAcrossRestartAndDetectsRollback()
    {
        using var fixture = new RuntimeFixture();
        byte[] rolledBackState;

        await using (var first = fixture.CreateAccessor())
        {
            await CreateAccountAsync(await first.GetAccountsAsync());
            var host = await first.GetContactResolvePrivacyHostBindingAsync();
            Assert.Equal(OnionEntropyCommitOutcome.Committed,
                await host.EntropyLedger.CommitAsync(
                    EntropyBatch(Bytes(32, 0xA1)), CancellationToken.None));
            rolledBackState = File.ReadAllBytes(fixture.EntropyStatePath);
        }

        await using (var restarted = fixture.CreateAccessor())
        {
            var host = await restarted.GetContactResolvePrivacyHostBindingAsync();
            Assert.Equal(OnionEntropyCommitOutcome.Duplicate,
                await host.EntropyLedger.CommitAsync(
                    EntropyBatch(Bytes(32, 0xA1)), CancellationToken.None));
            Assert.Equal(OnionEntropyCommitOutcome.Committed,
                await host.EntropyLedger.CommitAsync(
                    EntropyBatch(Bytes(32, 0xA2)), CancellationToken.None));
        }

        File.WriteAllBytes(fixture.EntropyStatePath, rolledBackState);
        CryptographicOperations.ZeroMemory(rolledBackState);
        await using var afterRollback = fixture.CreateAccessor();
        await Assert.ThrowsAsync<CryptographicException>(() =>
            afterRollback.GetContactResolvePrivacyHostBindingAsync());
    }

    [Fact]
    public async Task EntropyLedgerCorruptionAndDisposedOwnerFailClosed()
    {
        using var fixture = new RuntimeFixture();
        DeepContactResolvePrivacyHostBinding binding;
        await using (var first = fixture.CreateAccessor())
        {
            await CreateAccountAsync(await first.GetAccountsAsync());
            binding = await first.GetContactResolvePrivacyHostBindingAsync();
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await binding.EntropyLedger.CommitAsync(
                EntropyBatch(Bytes(32, 0xB1)), CancellationToken.None));

        var corrupted = File.ReadAllBytes(fixture.EntropyStatePath);
        corrupted[^1] ^= 0x80;
        File.WriteAllBytes(fixture.EntropyStatePath, corrupted);
        CryptographicOperations.ZeroMemory(corrupted);
        await using var reopened = fixture.CreateAccessor();
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            reopened.GetContactResolvePrivacyHostBindingAsync());
    }

    [Fact]
    public async Task OpaqueVaultAcceptsOnlyCurrentAccountDeviceHandleAndZeroizableOutput()
    {
        using var fixture = new RuntimeFixture();
        await using var accessor = fixture.CreateAccessor();
        var created = await CreateAccountAsync(await accessor.GetAccountsAsync());
        var binding = await accessor.GetContactResolvePrivacyHostBindingAsync();
        byte[] instanceId;
        using (var storage = fixture.OpenStorage())
        using (var owned = await storage.ReadOwnedAsync(
                   created.Identity.SecureSlots.MessageStoreInstanceId))
        {
            Assert.NotNull(owned);
            instanceId = new byte[owned!.Length];
            owned.CopyTo(instanceId);
        }

        var scope = new ContactResolvePrivacyHostScope(created.Identity, instanceId);
        var authority = new OnionKeyAgreementAuthority(binding.KeyAgreementVault);
        var handle = authority.BindKeyHandle(scope.KeyHandleId);
        var peerScalar = Bytes(32, 0xC1);
        var peerPublic = ScalarMult.Base(peerScalar);
        var localPublic = created.Identity.Device.AgreementPublicKey.ToArray();
        byte[]? shared = null;
        byte[]? expected = null;
        try
        {
            shared = await binding.KeyAgreementVault.DeriveX25519SharedSecretAsync(
                handle, peerPublic, CancellationToken.None);
            expected = ScalarMult.Mult(peerScalar, localPublic);
            Assert.Equal(expected, shared);

            var wrongHandle = authority.BindKeyHandle(Bytes(32, 0xC2));
            await Assert.ThrowsAsync<CryptographicException>(async () =>
                await binding.KeyAgreementVault.DeriveX25519SharedSecretAsync(
                    wrongHandle, peerPublic, CancellationToken.None));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(instanceId);
            CryptographicOperations.ZeroMemory(peerScalar);
            CryptographicOperations.ZeroMemory(peerPublic);
            CryptographicOperations.ZeroMemory(localPublic);
            if (shared is not null) CryptographicOperations.ZeroMemory(shared);
            if (expected is not null) CryptographicOperations.ZeroMemory(expected);
        }
    }

    [Fact]
    public void ProductionCompositionHasNoRawDirectoryProofInputOrPublicBypass()
    {
        var properties = typeof(ContactResolveRuntimePrerequisites).GetProperties();
        Assert.DoesNotContain(properties, static property =>
            property.Name.Contains("Adp", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Dtt", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Proof", StringComparison.OrdinalIgnoreCase)
            || property.PropertyType == typeof(byte[])
            || property.PropertyType == typeof(ReadOnlyMemory<byte>)
            || property.PropertyType == typeof(VerifiedContactResolverPlacementContext));
        Assert.Empty(typeof(ProductionContactResolveRuntimeComposition).GetMethods(
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.DeclaredOnly));
        var compose = Assert.Single(typeof(ProductionContactResolveRuntimeComposition)
            .GetMethods(
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.DeclaredOnly),
                static method => method.Name == "TryCreate");
        Assert.Equal(
            [typeof(DeepContactResolvePersistenceBinding),
                typeof(ContactResolveRuntimePrerequisites)],
            compose.GetParameters().Select(static parameter => parameter.ParameterType));
        Assert.Empty(typeof(DeepContactResolveRuntimeAccessor).GetConstructors(
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance));
    }

    [Fact]
    public async Task ProductionRuntimeDisposesOwnedPrivacyTransportOnce()
    {
        using var fixture = new RuntimeFixture();
        await using var accountRuntime = fixture.CreateAccessor();
        await CreateAccountAsync(await accountRuntime.GetAccountsAsync());
        var persistence = await accountRuntime.GetContactResolvePersistenceBindingAsync();
        var primaryIngress = new TrackingPrivacyIngress();
        var fallbackIngress = new TrackingPrivacyIngress();
        var primaryRoute = new PrivacyMailboxRoute(
            new Uri("https://primary.example/"),
            new NoUsePathProvider());
        var fallbackRoute = new PrivacyMailboxRoute(
            new Uri("https://fallback.example/"),
            new NoUsePathProvider());
        var codec = new PrivacyRoutingCodec(
            new OnionEntropyAuthority(new NoUseEntropyLedger()),
            new OnionKeyAgreementAuthority(new NoUseKeyAgreementVault()));
        var transport = new PrivacyRoutedContactResolverTransport(
            primaryRoute,
            fallbackRoute,
            codec,
            primaryIngress,
            fallbackIngress);
        var verifier = new ContactResolverTrustedVerifier(
            new NoUseVerifiedCapabilitySource());
        var prerequisites = new ContactResolveRuntimePrerequisites(
            new XPointNetworkGenesisPin(NetworkId, Bytes(32, 0x92)),
            new FixedMonotonicClock(),
            () => transport,
            () => verifier,
            () => new NoUsePathAuthoritySource());

        var composition = ProductionContactResolveRuntimeComposition.TryCreate(
            persistence,
            prerequisites);

        Assert.Null(composition.UnavailableReason);
        Assert.NotNull(composition.Runtime);
        composition.Runtime.Dispose();
        composition.Runtime.Dispose();
        Assert.Equal(1, primaryIngress.DisposeCalls);
        Assert.Equal(1, fallbackIngress.DisposeCalls);
    }

    private static async Task<DeepAccountCreationResult> CreateAccountAsync(DeepAccountService accounts)
    {
        using var draft = accounts.PrepareCreate("Alice");
        byte[]? recovery = null;
        draft.RevealCanonicalPhraseOnce(bytes => recovery = bytes.ToArray());
        try
        {
            using var confirmation = DeepOwnedRecoveryPhraseUtf8.CopyFrom(recovery!);
            return await accounts.CommitPreparedAsync(draft, confirmation);
        }
        finally
        {
            if (recovery is not null) CryptographicOperations.ZeroMemory(recovery);
        }
    }

    private static string CanonicalDia(byte[] networkId, ulong expiresAt)
    {
        ReadOnlyMemory<byte>[] fields =
        [
            networkId,
            Bytes(32, 0x71),
            new byte[] { 2 },
            U16(1),
            Bytes(16, 0x72),
            Bytes(32, 0x73),
            Bytes(32, 0x74),
            U64(expiresAt),
            U16(1),
        ];
        var output = new byte[12 + fields.Sum(static field => 8 + field.Length)];
        Encoding.ASCII.GetBytes("DIA1").CopyTo(output, 0);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6), 0x0201);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8), checked((ushort)fields.Length));
        var offset = 12;
        for (var index = 0; index < fields.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(offset), checked((ushort)(index + 1)));
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(offset + 4), checked((uint)fields[index].Length));
            fields[index].Span.CopyTo(output.AsSpan(offset + 8));
            offset += 8 + fields[index].Length;
        }
        return DeepInvitationTextCodec.EncodeCanonical(ContactCodec.Decode("DIA1", output));
    }

    private static string ContactKeySlot(string messageStoreInstanceSlot)
    {
        const string suffix = ".message-store-instance";
        Assert.StartsWith("deep.store.v1.", messageStoreInstanceSlot, StringComparison.Ordinal);
        Assert.EndsWith(suffix, messageStoreInstanceSlot, StringComparison.Ordinal);
        return messageStoreInstanceSlot[..^suffix.Length] + ".contact-state-key";
    }

    private static string ScopedSlot(string messageStoreInstanceSlot, string suffix)
    {
        const string sourceSuffix = ".message-store-instance";
        Assert.EndsWith(sourceSuffix, messageStoreInstanceSlot, StringComparison.Ordinal);
        return messageStoreInstanceSlot[..^sourceSuffix.Length] + suffix;
    }

    private static ContactResolveRuntimePrerequisites PrerequisitesMissing(
        ContactResolveRuntimeUnavailableReason reason)
    {
        if (reason is ContactResolveRuntimeUnavailableReason.MalformedConfiguration
            or ContactResolveRuntimeUnavailableReason.CrossNetworkConfiguration)
        {
            return new ContactResolveRuntimePrerequisites(
                GenesisPin: null,
                MonotonicClock: null,
                PrivacyRoutedTransportFactory: null,
                TrustedAuthorityVerifierFactory: null,
                PlacementContextSourceFactory: null,
                UnavailableReason: reason);
        }
        var genesis = reason == ContactResolveRuntimeUnavailableReason.GenesisPin
            ? null
            : new XPointNetworkGenesisPin(Bytes(16, 0x91), Bytes(32, 0x92));
        IOnionMonotonicClock? clock = reason is
            ContactResolveRuntimeUnavailableReason.GenesisPin or
            ContactResolveRuntimeUnavailableReason.MonotonicClock
                ? null
                : new FixedMonotonicClock();
        Func<PrivacyRoutedContactResolverTransport>? privacy = reason is
            ContactResolveRuntimeUnavailableReason.GenesisPin or
            ContactResolveRuntimeUnavailableReason.MonotonicClock or
            ContactResolveRuntimeUnavailableReason.PrivacyRoute
                ? null
                : static () => throw new InvalidOperationException("must-not-create-route");
        Func<ContactResolverTrustedVerifier>? authority = reason is
            ContactResolveRuntimeUnavailableReason.GenesisPin or
            ContactResolveRuntimeUnavailableReason.MonotonicClock or
            ContactResolveRuntimeUnavailableReason.PrivacyRoute or
            ContactResolveRuntimeUnavailableReason.AuthoritySource
                ? null
                : static () => throw new InvalidOperationException("must-not-create-verifier");
        Func<IContactResolvePlacementContextSource>? directory =
            reason == ContactResolveRuntimeUnavailableReason.DirectorySource
            ? null
            : static () => throw new InvalidOperationException("must-not-mint-placement");
        return new ContactResolveRuntimePrerequisites(
            genesis,
            clock,
            privacy,
            authority,
            directory);
    }

    private static string[] ContactDatabaseFamily(string path) =>
        [path, path + "-wal", path + "-shm", path + "-journal"];
    private static OnionEntropyCommitmentBatch EntropyBatch(params byte[][] commitments)
    {
        var constructor = Assert.Single(typeof(OnionEntropyCommitmentBatch)
            .GetConstructors(System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic));
        return (OnionEntropyCommitmentBatch)constructor.Invoke([false, commitments]);
    }
    private static byte[] Bytes(int length, byte value) => Enumerable.Repeat(value, length).ToArray();
    private static byte[] U16(ushort value) { var bytes = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); return bytes; }
    private static byte[] U64(ulong value) { var bytes = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(bytes, value); return bytes; }

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(),
            "deep-contact-maui-" + Guid.NewGuid().ToString("N"));

        internal RuntimeFixture() => Directory.CreateDirectory(directory);
        internal int NetworkIdReads { get; private set; }
        internal int SecureStorageCreates { get; private set; }
        internal string ContactStatePath => Path.Combine(directory, "deep-store-v1", "contacts.dcv1");
        internal string AccountDirectoryStatePath => Path.Combine(
            directory,
            "deep-store-v1",
            "account-directory.ads1");
        internal string EntropyStatePath => Path.Combine(
            directory,
            "deep-store-v1",
            ProtectedContactResolveEntropyLedger.StateFileName);

        internal DeepAccountRuntimeAccessor CreateAccessor() => new(
            directory,
            new FrozenClock(DateTimeOffset.Parse("2026-09-07T00:00:00Z")),
            () =>
            {
                NetworkIdReads++;
                return NetworkId.ToArray();
            },
            root =>
            {
                SecureStorageCreates++;
                return CreateStorage(root);
            },
            static () => new TestSecretProtector());

        internal JournaledDeepSecureStorage OpenStorage() => CreateStorage(directory);

        private static JournaledDeepSecureStorage CreateStorage(string root) => new(
            Path.Combine(root, "deep-store-v1", "secure-storage.dss"),
            new TestSecretProtector());

        public void Dispose()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixedPrerequisitesSource(ContactResolveRuntimePrerequisites value)
        : IContactResolveRuntimePrerequisitesSource
    {
        internal int Calls { get; private set; }

        public ValueTask<ContactResolveRuntimePrerequisites> GetCurrentAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(value);
        }
    }

    private sealed class FixedMonotonicClock : IOnionMonotonicClock
    {
        public ValueTask<OnionMonotonicReading> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new OnionMonotonicReading(Bytes(16, 0xA1), 1));
        }
    }

    private sealed class NoUsePathProvider : IPrivacyMailboxPathProvider
    {
        public ValueTask<PrivacyMailboxOnionAttempt> PrepareAsync(
            OnionOperation operation,
            ReadOnlyMemory<byte> exactCanonicalRequest,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<PrivacyMailboxOnionAttempt>(
                new InvalidOperationException("The disposal test must not prepare a route."));
    }

    private sealed class NoUsePathAuthoritySource :
        IContactResolvePathAuthoritySource,
        IContactResolvePlacementContextSource
    {
        public ValueTask<ContactResolvePathAuthority> GetCurrentAsync(
            Xiq1Request request,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<ContactResolvePathAuthority>(
                new InvalidOperationException("The composition test must not load authority."));

        public ValueTask<VerifiedContactResolverPlacementContext> MintPlacementContextAsync(
            ContactStoreScope accountScope,
            PendingContactAddress pendingAddress,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<VerifiedContactResolverPlacementContext>(
                new InvalidOperationException("The composition test must not mint placement."));
    }

    private sealed class FixedVerifiedIngressSource(
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

    private sealed class TrackingPrivacyIngress : IPrivacyManagedIngressTransport
    {
        internal int DisposeCalls { get; private set; }

        public Task<ReadOnlyMemory<byte>> ForwardAsync(
            ReadOnlyMemory<byte> opaqueFrame,
            CancellationToken cancellationToken) =>
            Task.FromException<ReadOnlyMemory<byte>>(
                new InvalidOperationException("The disposal test must not dispatch."));

        public void Dispose() => DisposeCalls++;
    }

    private sealed class NoUseEntropyLedger : IOnionEntropyUniquenessLedger
    {
        public ValueTask<OnionEntropyCommitOutcome> CommitAsync(
            OnionEntropyCommitmentBatch batch,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<OnionEntropyCommitOutcome>(
                new InvalidOperationException("The disposal test must not reserve entropy."));
    }

    private sealed class NoUseKeyAgreementVault : IOnionKeyAgreementVault
    {
        public ValueTask<byte[]> DeriveX25519SharedSecretAsync(
            OnionKeyHandle keyHandle,
            ReadOnlyMemory<byte> peerPublicKey,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<byte[]>(
                new InvalidOperationException("The disposal test must not derive a key."));
    }

    private sealed class NoUseVerifiedCapabilitySource
        : IContactResolverVerifiedCapabilitySource
    {
        public ValueTask<ContactResolverVerifiedCapabilitySet> VerifyAsync(
            ContactResolverVerificationInput input,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ContactResolverVerifiedCapabilitySet>(
                new InvalidOperationException("The disposal test must not verify capabilities."));
    }

    private sealed class TestSecretProtector : IDeepSecretProtector
    {
        private static readonly byte[] Key = SHA256.HashData("Deep/Test/ContactRuntime"u8);

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            if (plaintext.IsEmpty) throw new ArgumentException("Plaintext is required.", nameof(plaintext));
            var nonce = RandomNumberGenerator.GetBytes(12);
            var output = new byte[4 + nonce.Length + plaintext.Length + 16];
            "TDS1"u8.CopyTo(output);
            nonce.CopyTo(output, 4);
            using var aes = new AesGcm(Key, 16);
            aes.Encrypt(nonce, plaintext, output.AsSpan(16, plaintext.Length),
                output.AsSpan(16 + plaintext.Length, 16), "Deep/Test/ContactRuntime/V1"u8);
            CryptographicOperations.ZeroMemory(nonce);
            return output;
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
        {
            if (protectedBytes.Length < 32 || !protectedBytes[..4].SequenceEqual("TDS1"u8))
                throw new CryptographicException("Test secure-storage envelope is invalid.");
            var plaintextLength = protectedBytes.Length - 32;
            var output = new byte[plaintextLength];
            using var aes = new AesGcm(Key, 16);
            aes.Decrypt(protectedBytes.Slice(4, 12), protectedBytes.Slice(16, plaintextLength),
                protectedBytes[^16..], output, "Deep/Test/ContactRuntime/V1"u8);
            return output;
        }
    }
}
