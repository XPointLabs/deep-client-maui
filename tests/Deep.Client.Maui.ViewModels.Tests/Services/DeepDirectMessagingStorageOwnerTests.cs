using System.Reflection;
using System.Security.Cryptography;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Domain.ContactV1;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Persistence.MessagingCryptoV1;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.MessagingV1;
using Deep.Protocol.DeepNative;
using Deep.Protocol.Identity;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

[Collection("SQLite global pool isolation")]
public sealed class DeepDirectMessagingStorageOwnerTests
{
    private static readonly byte[] NetworkId = Enumerable.Range(1, 16)
        .Select(static value => (byte)value)
        .ToArray();

    [Fact]
    public async Task MissingAccountOrVerifiedAuthoritiesCreateNoMessagingState()
    {
        using var fixture = new RuntimeFixture();
        await using var accessor = fixture.CreateAccessor();

        Assert.Null(await accessor.TryGetDirectMessagingStorageAsync(null));
        Assert.False(File.Exists(fixture.PreKeyPath));
        Assert.False(File.Exists(fixture.CatalogPath));

        await CreateAccountAsync(await accessor.GetAccountsAsync());
        Assert.Null(await accessor.TryGetDirectMessagingStorageAsync(null));
        Assert.False(File.Exists(fixture.PreKeyPath));
        Assert.False(File.Exists(fixture.CatalogPath));
    }

    [Fact]
    public async Task OwnerCreatesEncryptedDistinctStoresAndTypedSessionCatalog()
    {
        using var fixture = new RuntimeFixture();
        await using var accessor = fixture.CreateAccessor();
        var identity = (await CreateAccountAsync(await accessor.GetAccountsAsync())).Identity;
        var local = LocalAuthority(identity);
        var owner = Assert.IsType<DeepDirectMessagingStorageOwner>(
            await accessor.TryGetDirectMessagingStorageAsync(local));

        AssertEncrypted(fixture.PreKeyPath);
        AssertEncrypted(fixture.CatalogPath);
        Assert.Null(await owner.TryOpenSessionAsync(null, createIfMissing: true));
        var missing = Session(NetworkId, 0x81);
        Assert.Null(await owner.TryOpenSessionAsync(missing, createIfMissing: false));
        Assert.Empty(await owner.ReadCatalogAsync());

        var requested = Session(NetworkId, 0x31);
        var opened = Assert.IsType<DeepDirectMessagingSessionStoreBinding>(
            await owner.TryOpenSessionAsync(requested, createIfMissing: true));
        Assert.True(opened.Store.OwnsSession(requested.ExactDph2Id));
        Assert.True(opened.Store.OwnsContactInitialSession(
            identity.Account.AccountIdentity.AccountId.Bytes.Span,
            identity.Device.DeviceId.Bytes.Span,
            identity.Device.DeviceGeneration,
            requested.ConversationId,
            requested.ExactDph2Id));

        var entry = Assert.Single(await owner.ReadCatalogAsync());
        Assert.True(entry.RemoteAccountId.Span.SequenceEqual(requested.RemoteAccountId));
        Assert.Equal(requested.RemoteAccountGeneration, entry.RemoteAccountGeneration);
        Assert.True(entry.RemoteDeviceId.Span.SequenceEqual(requested.RemoteDeviceId));
        Assert.Equal(requested.RemoteDeviceGeneration, entry.RemoteDeviceGeneration);
        Assert.True(entry.ConversationId.Span.SequenceEqual(requested.ConversationId));
        Assert.True(entry.ExactDph2Id.Span.SequenceEqual(requested.ExactDph2Id));

        var sessionPath = Assert.Single(Directory.GetFiles(fixture.SessionsPath, "*.mcr1"));
        AssertEncrypted(sessionPath);

        var preKey = await fixture.ReadSecretAsync(
            ReplaceMessageStoreSuffix(identity.SecureSlots.MessageStoreInstanceId,
                ".direct-prekey-v1-key"));
        var catalog = await fixture.ReadSecretAsync(
            ReplaceMessageStoreSuffix(identity.SecureSlots.MessageStoreInstanceId,
                ".direct-session-catalog-key"));
        var catalogKey = requested.ComputeCatalogKey(local);
        var session = await fixture.ReadSecretAsync(
            ReplaceMessageStoreSuffix(identity.SecureSlots.MessageStoreInstanceId,
                ".direct-session-key." + Convert.ToHexStringLower(catalogKey)));
        try
        {
            Assert.NotNull(preKey);
            Assert.NotNull(catalog);
            Assert.NotNull(session);
            Assert.False(CryptographicOperations.FixedTimeEquals(preKey!, catalog!));
            Assert.False(CryptographicOperations.FixedTimeEquals(preKey!, session!));
            Assert.False(CryptographicOperations.FixedTimeEquals(catalog!, session!));
        }
        finally
        {
            Zero(preKey);
            Zero(catalog);
            Zero(session);
            CryptographicOperations.ZeroMemory(catalogKey);
        }
    }

    [Fact]
    public async Task RuntimeUsesCanonicalDpd1ReferenceAndIncompleteVerifiedFlowsFailClosed()
    {
        using var fixture = new RuntimeFixture();
        await using var accessor = fixture.CreateAccessor();
        var identity = (await CreateAccountAsync(await accessor.GetAccountsAsync())).Identity;
        var local = LocalAuthority(identity);
        var owner = Assert.IsType<DeepDirectMessagingStorageOwner>(
            await accessor.TryGetDirectMessagingStorageAsync(local));

        var reference = owner.PreKeyOwner.Scope.Dpd1Reference.ToArray();
        Assert.Equal(38, reference.Length);
        Assert.True(reference.AsSpan(0, 4).SequenceEqual("DPD1"u8));
        Assert.Equal(0, reference[4]);
        Assert.Equal(1, reference[5]);
        Assert.True(reference.AsSpan(6).SequenceEqual(local.ExactDpd1Hash));
        Assert.False(owner.HasProductionInventoryOwner);

        Assert.Null(await accessor.EnsureDirectMessagingInventoryAsync(local, null));
        Assert.Null(await accessor.TryPrepareDirectMessagingInitiatorClaimAsync(
            local, null, null, null, 64));
        Assert.Null(await accessor.TryCommitDirectMessagingInitiatorSessionAsync(
            local, null, null, ReadOnlyMemory<byte>.Empty));
        Assert.Null(await accessor.TryCommitDirectMessagingResponderSessionAsync(
            local, null, null, null, null, 64));
        Assert.Empty(await owner.ReadCatalogAsync());
        Assert.False(Directory.Exists(fixture.SessionsPath));
    }

    [Fact]
    public async Task CatalogAndPerSessionStoreSurviveRestartAndRequireSameVerifiedTuple()
    {
        using var fixture = new RuntimeFixture();
        var requested = Session(NetworkId, 0x41);
        DeepDirectMessagingSessionStoreBinding firstBinding;
        DeepLocalIdentitySnapshot identity;

        await using (var first = fixture.CreateAccessor())
        {
            identity = (await CreateAccountAsync(await first.GetAccountsAsync())).Identity;
            var owner = Assert.IsType<DeepDirectMessagingStorageOwner>(
                await first.TryGetDirectMessagingStorageAsync(LocalAuthority(identity)));
            firstBinding = Assert.IsType<DeepDirectMessagingSessionStoreBinding>(
                await owner.TryOpenSessionAsync(requested, createIfMissing: true));
        }

        Assert.True(firstBinding.Store.IsKeyZeroedForTesting);
        await using var restarted = fixture.CreateAccessor();
        var current = Assert.IsType<DeepLocalIdentitySnapshot>(
            await (await restarted.GetAccountsAsync()).GetLocalIdentityAsync());
        var reopenedOwner = Assert.IsType<DeepDirectMessagingStorageOwner>(
            await restarted.TryGetDirectMessagingStorageAsync(LocalAuthority(current)));
        var entries = await reopenedOwner.ReadCatalogAsync();
        Assert.Single(entries);
        var reopened = Assert.IsType<DeepDirectMessagingSessionStoreBinding>(
            await reopenedOwner.TryOpenSessionAsync(requested, createIfMissing: false));
        Assert.True(reopened.Store.OwnsSession(requested.ExactDph2Id));

        var conflicting = DeepDirectMessagingVerifiedSessionBinding.CreateForTests(
            NetworkId,
            requested.RemoteAccountId,
            requested.RemoteAccountGeneration,
            requested.RemoteDeviceId,
            requested.RemoteDeviceGeneration,
            ContactConversationId32.FromBytes(requested.ConversationId),
            Bytes(32, 0xE2));
        await Assert.ThrowsAsync<CryptographicException>(async () =>
            await reopenedOwner.TryOpenSessionAsync(conflicting, createIfMissing: true));
        Assert.Single(await reopenedOwner.ReadCatalogAsync());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task WrongAccountOrGenerationAuthorityFailsClosed(
        bool wrongAccount,
        bool wrongGeneration)
    {
        using var fixture = new RuntimeFixture();
        await using var accessor = fixture.CreateAccessor();
        var identity = (await CreateAccountAsync(await accessor.GetAccountsAsync())).Identity;
        var account = wrongAccount
            ? Bytes(32, 0xF1)
            : identity.Account.AccountIdentity.AccountId.Bytes.ToArray();
        var generation = wrongGeneration
            ? checked(identity.Account.AccountIdentity.AccountGeneration + 1)
            : identity.Account.AccountIdentity.AccountGeneration;
        var authority = DeepDirectMessagingLocalAuthorityBinding.CreateForTests(
            identity.NetworkId.Span,
            account,
            generation,
            identity.Device.DeviceId.Bytes.Span,
            identity.Device.DeviceGeneration,
            Bytes(32, 0xD1));

        await Assert.ThrowsAsync<CryptographicException>(async () =>
            await accessor.TryGetDirectMessagingStorageAsync(authority));
        Assert.False(File.Exists(fixture.PreKeyPath));
        Assert.False(File.Exists(fixture.CatalogPath));
        CryptographicOperations.ZeroMemory(account);
    }

    [Fact]
    public async Task WrongNetworkPeerCannotCreateCatalogEntry()
    {
        using var fixture = new RuntimeFixture();
        await using var accessor = fixture.CreateAccessor();
        var identity = (await CreateAccountAsync(await accessor.GetAccountsAsync())).Identity;
        var owner = Assert.IsType<DeepDirectMessagingStorageOwner>(
            await accessor.TryGetDirectMessagingStorageAsync(LocalAuthority(identity)));
        var wrongNetwork = Bytes(16, 0xF2);

        await Assert.ThrowsAsync<CryptographicException>(async () =>
            await owner.TryOpenSessionAsync(Session(wrongNetwork, 0x51), createIfMissing: true));
        Assert.Empty(await owner.ReadCatalogAsync());
        Assert.False(Directory.Exists(fixture.SessionsPath));
    }

    [Fact]
    public async Task CorruptCatalogRequiresResetAndPreservesEncryptedFiles()
    {
        using var fixture = new RuntimeFixture();
        await using (var first = fixture.CreateAccessor())
        {
            var identity = (await CreateAccountAsync(await first.GetAccountsAsync())).Identity;
            var owner = Assert.IsType<DeepDirectMessagingStorageOwner>(
                await first.TryGetDirectMessagingStorageAsync(LocalAuthority(identity)));
            Assert.NotNull(await owner.TryOpenSessionAsync(Session(NetworkId, 0x61), true));
        }

        fixture.CorruptCatalog();
        await using var restarted = fixture.CreateAccessor();
        var identityAfterRestart = Assert.IsType<DeepLocalIdentitySnapshot>(
            await (await restarted.GetAccountsAsync()).GetLocalIdentityAsync());
        var error = await Assert.ThrowsAsync<LocalStateResetRequiredException>(async () =>
            await restarted.TryGetDirectMessagingStorageAsync(LocalAuthority(identityAfterRestart)));

        Assert.True(error.Reason is LocalStateResetRequiredReason.InvalidCurrentSchema or
            LocalStateResetRequiredReason.UnreadableOrWrongKey);
        Assert.True(File.Exists(fixture.CatalogPath));
        Assert.Single(Directory.GetFiles(fixture.SessionsPath, "*.mcr1"));
    }

    [Fact]
    public async Task ResetPurgesAllStoresAndKeysAndZeroizesOpenOwners()
    {
        using var fixture = new RuntimeFixture();
        await using var accessor = fixture.CreateAccessor();
        var identity = (await CreateAccountAsync(await accessor.GetAccountsAsync())).Identity;
        var local = LocalAuthority(identity);
        var owner = Assert.IsType<DeepDirectMessagingStorageOwner>(
            await accessor.TryGetDirectMessagingStorageAsync(local));
        var requested = Session(NetworkId, 0x71);
        var session = Assert.IsType<DeepDirectMessagingSessionStoreBinding>(
            await owner.TryOpenSessionAsync(requested, true));
        var preKeySlot = ReplaceMessageStoreSuffix(
            identity.SecureSlots.MessageStoreInstanceId,
            ".direct-prekey-v1-key");
        var catalogSlot = ReplaceMessageStoreSuffix(
            identity.SecureSlots.MessageStoreInstanceId,
            ".direct-session-catalog-key");
        var key = requested.ComputeCatalogKey(local);
        var sessionSlot = ReplaceMessageStoreSuffix(
            identity.SecureSlots.MessageStoreInstanceId,
            ".direct-session-key." + Convert.ToHexStringLower(key));
        CryptographicOperations.ZeroMemory(key);

        await accessor.ResetLocalStateAsync();

        Assert.True(owner.IsPreKeyOwnerKeyZeroedForTesting);
        Assert.True(owner.IsCatalogKeyZeroedForTesting);
        Assert.True(session.Store.IsKeyZeroedForTesting);
        Assert.False(File.Exists(fixture.PreKeyPath));
        Assert.False(File.Exists(fixture.CatalogPath));
        Assert.False(Directory.Exists(fixture.SessionsPath));
        Assert.Null(await fixture.ReadSecretAsync(preKeySlot));
        Assert.Null(await fixture.ReadSecretAsync(catalogSlot));
        Assert.Null(await fixture.ReadSecretAsync(sessionSlot));
    }

    [Fact]
    public void NewOwnerApiContainsNoLegacySessionIdentityOrRecoveryMaterial()
    {
        var ownedTypes = new[]
        {
            typeof(DeepDirectMessagingStorageOwner),
            typeof(DeepDirectMessagingLocalAuthorityBinding),
            typeof(DeepDirectMessagingVerifiedSessionBinding),
            typeof(DeepDirectMessagingSessionStoreBinding),
            typeof(DeepDirectMessagingSessionCatalogEntry),
            typeof(DeepDirectMessagingInventoryPublication),
            typeof(DeepDirectMessagingInitiatorClaimPreparation),
            typeof(DeepDirectMessagingInitiatorCommitResult),
            typeof(DeepAccountRuntimeOwner),
            typeof(DeepAccountRuntimeAccessor),
        };
        var signatures = ownedTypes
            .SelectMany(static type => type.GetMembers(
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic))
            .Select(static member => member.ToString() ?? string.Empty)
            .ToArray();
        var memberNames = ownedTypes
            .SelectMany(static type => type.GetMembers(
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic))
            .Select(static member => member.Name)
            .ToArray();

        Assert.DoesNotContain("SessionId", memberNames);
        Assert.DoesNotContain(signatures,
            static signature => signature.Contains(
                "Deep.Client.Shared.Domain.SessionId",
                StringComparison.Ordinal));
        Assert.DoesNotContain(signatures,
            static signature => signature.Contains("RecoveryPhrase", StringComparison.Ordinal));
        Assert.DoesNotContain(signatures,
            static signature => signature.Contains("TransportAccountAlias", StringComparison.Ordinal));

        var productionAuthorityFactory = Assert.Single(
            typeof(DeepDirectMessagingLocalAuthorityBinding).GetMethods(
                BindingFlags.Static | BindingFlags.NonPublic),
            static method => method.Name == "FromVerified");
        var expectedProductionFactoryParameters = new[]
        {
            typeof(LocalDeviceX25519AgreementAuthority),
            typeof(VerifiedDeviceRelative),
        };
        Assert.Equal(
            expectedProductionFactoryParameters,
            productionAuthorityFactory.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());
        var inventoryOwnerField = typeof(DeepDirectMessagingStorageOwner).GetField(
            "inventoryOwner",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(inventoryOwnerField);
        Assert.Equal(
            "Deep.Client.Shared.Services.ContactV1.ProductionPreKeyV1InventoryOwner",
            inventoryOwnerField!.FieldType.FullName);
    }

    [Fact]
    public void InitiatorDurableDispatchResultOwnsCopiesAndZeroizesOnDispose()
    {
        var commitment = Bytes(32, 0x41);
        var journalHead = Bytes(32, 0x42);
        var exactDph2 = Bytes(192, 0x43);
        var operation = Bytes(32, 0x44);
        var exactDph2Id = Bytes(32, 0x45);
        var replay = Bytes(32, 0x46);
        try
        {
            var commit = new MessagingCryptoV1CommitResult(
                MessagingCryptoV1CommitDisposition.Initialized,
                1,
                commitment,
                1,
                journalHead,
                ForkLatched: false,
                TerminallyLatched: false);
            using var dispatch = new InitiatorInitialSessionDispatchEnvelope(
                exactDph2,
                operation,
                exactDph2Id,
                replay);
            var result = new DeepDirectMessagingInitiatorCommitResult(commit, dispatch);

            Assert.True(result.ExactDph2.Span.SequenceEqual(exactDph2));
            Assert.True(result.ClaimOperationId.Span.SequenceEqual(operation));
            Assert.True(result.FullReplayHash.Span.SequenceEqual(replay));
            result.Dispose();

            Assert.Throws<ObjectDisposedException>(() => _ = result.ExactDph2);
            Assert.Throws<ObjectDisposedException>(() => _ = result.ClaimOperationId);
            Assert.Throws<ObjectDisposedException>(() => _ = result.FullReplayHash);
            Assert.All(result.StateCommitment.ToArray(), static value => Assert.Equal(0, value));
            Assert.All(result.JournalHead.ToArray(), static value => Assert.Equal(0, value));
        }
        finally
        {
            Zero(commitment);
            Zero(journalHead);
            Zero(exactDph2);
            Zero(operation);
            Zero(exactDph2Id);
            Zero(replay);
        }
    }

    private static DeepDirectMessagingLocalAuthorityBinding LocalAuthority(
        DeepLocalIdentitySnapshot identity) =>
        DeepDirectMessagingLocalAuthorityBinding.CreateForTests(
            identity,
            Bytes(32, 0xD1));

    private static DeepDirectMessagingVerifiedSessionBinding Session(
        ReadOnlySpan<byte> networkId,
        byte marker) => DeepDirectMessagingVerifiedSessionBinding.CreateForTests(
            networkId,
            Bytes(32, checked((byte)(marker + 1))),
            1,
            Bytes(32, checked((byte)(marker + 2))),
            1,
            ContactConversationId32.FromBytes(Bytes(32, checked((byte)(marker + 3)))),
            Bytes(32, checked((byte)(marker + 4))));

    private static async Task<DeepAccountCreationResult> CreateAccountAsync(
        DeepAccountService accounts)
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
            Zero(recovery);
        }
    }

    private static void AssertEncrypted(string path)
    {
        Assert.True(File.Exists(path));
        Span<byte> header = stackalloc byte[16];
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal(header.Length, stream.Read(header));
        Assert.False(header.SequenceEqual("SQLite format 3\0"u8));
        CryptographicOperations.ZeroMemory(header);
    }

    private static string ReplaceMessageStoreSuffix(string slot, string replacement)
    {
        const string suffix = ".message-store-instance";
        Assert.EndsWith(suffix, slot, StringComparison.Ordinal);
        return slot[..^suffix.Length] + replacement;
    }

    private static byte[] Bytes(int length, byte marker) =>
        Enumerable.Repeat(marker, length).ToArray();

    private static void Zero(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private sealed class RuntimeFixture : IDisposable
    {
        internal RuntimeFixture()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "deep-direct-storage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        internal string DirectoryPath { get; }
        internal string PreKeyPath => Path.Combine(
            DirectoryPath, "deep-store-v1", "direct-prekeys.dpk2");
        internal string CatalogPath => Path.Combine(
            DirectoryPath, "deep-store-v1", "direct-sessions.dsc1");
        internal string SessionsPath => Path.Combine(
            DirectoryPath, "deep-store-v1", "direct-sessions");

        internal DeepAccountRuntimeAccessor CreateAccessor() => new(
            DirectoryPath,
            new FrozenClock(DateTimeOffset.Parse("2026-09-08T00:00:00Z")),
            () => NetworkId.ToArray(),
            CreateStorage,
            static () => new TestSecretProtector());

        internal async Task<byte[]?> ReadSecretAsync(string slot)
        {
            using var storage = CreateStorage(DirectoryPath);
            using var secret = await storage.ReadOwnedAsync(slot);
            if (secret is null)
            {
                return null;
            }
            var value = new byte[secret.Length];
            secret.CopyTo(value);
            return value;
        }

        internal void CorruptCatalog()
        {
            var bytes = File.ReadAllBytes(CatalogPath);
            try
            {
                Assert.True(bytes.Length > 128);
                bytes[97] ^= 0x5A;
                File.WriteAllBytes(CatalogPath, bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }

        private static JournaledDeepSecureStorage CreateStorage(string root) => new(
            Path.Combine(root, "deep-store-v1", "secure-storage.dss"),
            new TestSecretProtector());
    }

    private sealed class TestSecretProtector : IDeepSecretProtector
    {
        private static readonly byte[] Key =
            SHA256.HashData("Deep/Test/DirectMessagingStorageOwner"u8);

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var output = new byte[4 + nonce.Length + plaintext.Length + 16];
            "TDS1"u8.CopyTo(output);
            nonce.CopyTo(output, 4);
            using var aes = new AesGcm(Key, 16);
            aes.Encrypt(
                nonce,
                plaintext,
                output.AsSpan(16, plaintext.Length),
                output.AsSpan(16 + plaintext.Length, 16),
                "Deep/Test/DirectMessagingStorageOwner/V1"u8);
            CryptographicOperations.ZeroMemory(nonce);
            return output;
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
        {
            if (protectedBytes.Length < 32 || !protectedBytes[..4].SequenceEqual("TDS1"u8))
            {
                throw new CryptographicException(
                    "Test secure-storage envelope is invalid.");
            }
            var output = new byte[protectedBytes.Length - 32];
            using var aes = new AesGcm(Key, 16);
            aes.Decrypt(
                protectedBytes.Slice(4, 12),
                protectedBytes.Slice(16, output.Length),
                protectedBytes[^16..],
                output,
                "Deep/Test/DirectMessagingStorageOwner/V1"u8);
            return output;
        }
    }
}
