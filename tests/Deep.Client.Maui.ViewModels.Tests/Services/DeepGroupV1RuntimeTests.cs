using System.Security.Cryptography;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Domain.GroupV1;
using Deep.Client.Shared.Domain.DeviceV1;
using Deep.Client.Shared.Domain.MessagingV1;
using Deep.Client.Shared.Features;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Persistence.GroupV1;
using Deep.Client.Shared.Persistence.MessagingV1;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;
using Deep.Protocol.Identity;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class DeepGroupV1RuntimeTests
{
    private static readonly byte[] NetworkId = Enumerable.Range(1, 16)
        .Select(static value => (byte)value)
        .ToArray();

    [Fact]
    public async Task AccountOwnerOpensDistinctScopedGroupStoreAndSurvivesRestart()
    {
        using var fixture = new RuntimeFixture();
        GroupStoreScope firstScope;
        string keySlot;
        string activationKeySlot;
        SqliteGroupStateStore firstStore;
        SqliteGroupInvitationActivationStore firstActivationStore;

        await using (var first = fixture.CreateAccessor())
        {
            var created = await CreateAccountAsync(await first.GetAccountsAsync());
            keySlot = GroupKeySlot(created.Identity.SecureSlots.MessageStoreInstanceId);
            activationKeySlot = GroupActivationKeySlot(
                created.Identity.SecureSlots.MessageStoreInstanceId);
            var binding = Assert.IsType<DeepGroupV1RuntimeBinding>(
                await first.TryGetGroupV1RuntimeBindingAsync());
            firstStore = binding.StateStore;
            firstActivationStore = binding.InvitationActivationStore;
            firstScope = binding.StateStore.Scope;

            Assert.True(File.Exists(fixture.GroupStatePath));
            Assert.True(File.Exists(fixture.GroupActivationStatePath));
            Assert.Equal(firstScope, binding.InvitationActivationStore.Scope);
            Assert.Equal(created.Identity.Account.AccountIdentity.AccountId, firstScope.AccountId);
            Assert.Equal(created.Identity.Account.AccountIdentity.AccountGeneration,
                firstScope.AccountGeneration);
            Assert.True(binding.MessagingScope.LocalAccountId.Span.SequenceEqual(
                created.Identity.Account.AccountIdentity.AccountId.Bytes.Span));
            Assert.Equal(firstScope.AccountGeneration, binding.MessagingScope.DatabaseGeneration);
            Assert.DoesNotContain(
                typeof(DeepGroupV1RuntimeBinding).GetProperties(),
                static property => property.Name.Contains("Session", StringComparison.Ordinal) ||
                    property.PropertyType == typeof(SessionId));
            var groupKey = Assert.IsType<byte[]>(await fixture.ReadSecretAsync(keySlot));
            var activationKey = Assert.IsType<byte[]>(
                await fixture.ReadSecretAsync(activationKeySlot));
            var messageInstance = Assert.IsType<byte[]>(await fixture.ReadSecretAsync(
                created.Identity.SecureSlots.MessageStoreInstanceId));
            try
            {
                Assert.False(CryptographicOperations.FixedTimeEquals(groupKey, messageInstance));
                Assert.False(CryptographicOperations.FixedTimeEquals(
                    activationKey,
                    messageInstance));
                Assert.False(CryptographicOperations.FixedTimeEquals(groupKey, activationKey));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(groupKey);
                CryptographicOperations.ZeroMemory(activationKey);
                CryptographicOperations.ZeroMemory(messageInstance);
            }
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            firstStore.ReadHeadsAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            firstActivationStore.ReadAsync(
                GroupOperationId32.FromBytes(Bytes(32, 0x91))).AsTask());

        await using (var restarted = fixture.CreateAccessor())
        {
            var binding = Assert.IsType<DeepGroupV1RuntimeBinding>(
                await restarted.TryGetGroupV1RuntimeBindingAsync());
            Assert.Equal(firstScope, binding.StateStore.Scope);
            Assert.Equal(firstScope, binding.InvitationActivationStore.Scope);
            Assert.Empty(await binding.StateStore.ReadHeadsAsync());
        }
    }

    [Fact]
    public async Task ExistingInvitationActivationStoreWithoutProtectedKeyRequiresReset()
    {
        using var fixture = new RuntimeFixture();
        string activationKeySlot;
        await using (var first = fixture.CreateAccessor())
        {
            var created = await CreateAccountAsync(await first.GetAccountsAsync());
            activationKeySlot = GroupActivationKeySlot(
                created.Identity.SecureSlots.MessageStoreInstanceId);
            Assert.NotNull(await first.TryGetGroupV1RuntimeBindingAsync());
        }

        await fixture.DeleteSecretAsync(activationKeySlot);
        await using var restarted = fixture.CreateAccessor();
        var error = await Assert.ThrowsAsync<LocalStateResetRequiredException>(() =>
            restarted.TryGetGroupV1RuntimeBindingAsync());

        Assert.Equal(LocalStateResetRequiredReason.InvalidCurrentSchema, error.Reason);
        Assert.True(File.Exists(fixture.GroupActivationStatePath));
        Assert.Null(await fixture.ReadSecretAsync(activationKeySlot));
    }

    [Fact]
    public async Task ExistingGroupStoreRejectsWrongProtectedKeyWithoutDeletingState()
    {
        using var fixture = new RuntimeFixture();
        string keySlot;
        await using (var first = fixture.CreateAccessor())
        {
            var created = await CreateAccountAsync(await first.GetAccountsAsync());
            keySlot = GroupKeySlot(created.Identity.SecureSlots.MessageStoreInstanceId);
            Assert.NotNull(await first.TryGetGroupV1RuntimeBindingAsync());
        }

        await fixture.ReplaceSecretAsync(keySlot, Bytes(32, 0xE7));
        await using var restarted = fixture.CreateAccessor();
        var error = await Assert.ThrowsAsync<GroupStateStoreOpenException>(() =>
            restarted.TryGetGroupV1RuntimeBindingAsync());

        Assert.Equal(GroupStateStoreOpenFailure.UnreadableOrWrongKey, error.Reason);
        Assert.True(File.Exists(fixture.GroupStatePath));
        Assert.Equal(Bytes(32, 0xE7), await fixture.ReadSecretAsync(keySlot));
    }

    [Fact]
    public async Task ExistingGroupStoreWithoutProtectedKeyRequiresResetAndPreservesDatabase()
    {
        using var fixture = new RuntimeFixture();
        string keySlot;
        await using (var first = fixture.CreateAccessor())
        {
            var created = await CreateAccountAsync(await first.GetAccountsAsync());
            keySlot = GroupKeySlot(created.Identity.SecureSlots.MessageStoreInstanceId);
            Assert.NotNull(await first.TryGetGroupV1RuntimeBindingAsync());
        }

        await fixture.DeleteSecretAsync(keySlot);
        await using var restarted = fixture.CreateAccessor();
        var error = await Assert.ThrowsAsync<LocalStateResetRequiredException>(() =>
            restarted.TryGetGroupV1RuntimeBindingAsync());

        Assert.Equal(LocalStateResetRequiredReason.InvalidCurrentSchema, error.Reason);
        Assert.True(File.Exists(fixture.GroupStatePath));
        Assert.Null(await fixture.ReadSecretAsync(keySlot));
    }

    [Fact]
    public async Task ExistingGroupStoreRejectsAnotherAccountScope()
    {
        using var fixture = new RuntimeFixture();
        byte[]? key = null;
        await using (var accessor = fixture.CreateAccessor())
        {
            var created = await CreateAccountAsync(await accessor.GetAccountsAsync());
            var keySlot = GroupKeySlot(created.Identity.SecureSlots.MessageStoreInstanceId);
            Assert.NotNull(await accessor.TryGetGroupV1RuntimeBindingAsync());
            key = await fixture.ReadSecretAsync(keySlot);
        }

        try
        {
            var other = DeepAccountIdentityCapability.FromVerifiedInputs(
                DeepNetworkId16.FromVerifiedBytes(NetworkId),
                1,
                AccountEd25519PublicKey32.FromVerifiedBytes(Bytes(32, 0x91)));
            using var options = new SqliteGroupStateStoreOptions(
                fixture.GroupStatePath,
                key!,
                GroupStoreScope.ForCurrentAccount(other.AccountId, other.AccountGeneration),
                allowCreate: false);
            var error = Assert.Throws<GroupStateStoreOpenException>(() =>
                new SqliteGroupStateStore(options));
            Assert.Equal(GroupStateStoreOpenFailure.ScopeMismatch, error.Reason);
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    [Fact]
    public async Task ResetDisposesAndPurgesGroupStoreAndProtectedKey()
    {
        using var fixture = new RuntimeFixture();
        await using var accessor = fixture.CreateAccessor();
        var created = await CreateAccountAsync(await accessor.GetAccountsAsync());
        var keySlot = GroupKeySlot(created.Identity.SecureSlots.MessageStoreInstanceId);
        var activationKeySlot = GroupActivationKeySlot(
            created.Identity.SecureSlots.MessageStoreInstanceId);
        var binding = Assert.IsType<DeepGroupV1RuntimeBinding>(
            await accessor.TryGetGroupV1RuntimeBindingAsync());

        await accessor.ResetLocalStateAsync();

        Assert.DoesNotContain(fixture.GroupDatabaseFamily, File.Exists);
        Assert.DoesNotContain(fixture.GroupActivationDatabaseFamily, File.Exists);
        Assert.Null(await fixture.ReadSecretAsync(keySlot));
        Assert.Null(await fixture.ReadSecretAsync(activationKeySlot));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            binding.StateStore.ReadHeadsAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            binding.InvitationActivationStore.ReadAsync(
                GroupOperationId32.FromBytes(Bytes(32, 0x92))).AsTask());
    }

    [Fact]
    public async Task ComposerPassesAuthorityOnlyAfterDeepAccountExists()
    {
        using var fixture = new RuntimeFixture();
        await using var accessor = fixture.CreateAccessor();

        using (var noAccountRuntime = await CreateRuntimeAsync(fixture, accessor, "empty"))
        {
            Assert.False(noAccountRuntime.HasGroupV1FirstDispatchAuthority);
            Assert.DoesNotContain(fixture.GroupDatabaseFamily, File.Exists);
        }

        await CreateAccountAsync(await accessor.GetAccountsAsync());
        using var accountRuntime = await CreateRuntimeAsync(fixture, accessor, "account");

        Assert.True(accountRuntime.HasGroupV1FirstDispatchAuthority);
        Assert.True(File.Exists(fixture.GroupStatePath));
    }

    [Fact]
    public async Task AccountOwnerMountsDistinctScopedXpk1JournalAndDeviceStore()
    {
        using var fixture = new RuntimeFixture();
        string xpkKeySlot;
        string deviceKeySlot;

        await using (var first = fixture.CreateAccessor())
        {
            var created = await CreateAccountAsync(await first.GetAccountsAsync());
            xpkKeySlot = ReplaceMessageStoreSuffix(
                created.Identity.SecureSlots.MessageStoreInstanceId,
                ".xpk1-claim-journal-key");
            deviceKeySlot = ReplaceMessageStoreSuffix(
                created.Identity.SecureSlots.MessageStoreInstanceId,
                ".device-state-key");
            var journal = await first.GetXpk1ClaimJournalAsync();
            var devices = await first.GetDeviceStateStoreAsync();

            Assert.Equal(created.Identity.Account.AccountIdentity.AccountId,
                journal.Scope.AccountId);
            Assert.True(File.Exists(fixture.Xpk1JournalPath));
            Assert.True(File.Exists(fixture.DeviceStatePath));
            var read = await devices.ReadAsync(
                DeviceAccountId32.FromBytes(
                    created.Identity.Account.AccountIdentity.AccountId.Bytes.Span),
                created.Identity.Account.AccountIdentity.AccountGeneration,
                CancellationToken.None);
            Assert.Null(read.Snapshot);

            var xpkKey = Assert.IsType<byte[]>(await fixture.ReadSecretAsync(xpkKeySlot));
            var deviceKey = Assert.IsType<byte[]>(await fixture.ReadSecretAsync(deviceKeySlot));
            try
            {
                Assert.False(CryptographicOperations.FixedTimeEquals(xpkKey, deviceKey));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(xpkKey);
                CryptographicOperations.ZeroMemory(deviceKey);
            }
        }

        await using var restarted = fixture.CreateAccessor();
        var restartedAccounts = await restarted.GetAccountsAsync();
        var restartedIdentity = Assert.IsType<DeepLocalIdentitySnapshot>(
            await restartedAccounts.GetLocalIdentityAsync());
        Assert.Equal(
            restartedIdentity.Account.AccountIdentity.AccountId,
            (await restarted.GetXpk1ClaimJournalAsync()).Scope.AccountId);
        Assert.NotNull(await restarted.GetDeviceStateStoreAsync());
    }

    private static Task<ClientRuntime> CreateRuntimeAsync(
        RuntimeFixture fixture,
        DeepAccountRuntimeAccessor accessor,
        string suffix)
    {
        var transport = new AuthenticatedTransport();
        return PersistentClientRuntimeComposer.CreateAsync(
            Path.Combine(fixture.Directory, $"client-{suffix}.db"),
            ClientFeatureFlags.ReleaseDefaults with
            {
                MetadataPrivateTransportRequired = false
            },
            new FrozenClock(DateTimeOffset.Parse("2026-09-08T00:00:00Z")),
            new DisabledAvatarProfileTransport(),
            new string('A', 64),
            (_, _) => new StoreBoundRuntimeTransportComposition(
                transport,
                new DirectP2pMailboxDeliveryPolicy()),
            transportOutboxExecutor: null,
            accessor);
    }

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
            if (recovery is not null)
            {
                CryptographicOperations.ZeroMemory(recovery);
            }
        }
    }

    private static string GroupKeySlot(string messageStoreInstanceSlot) =>
        ReplaceMessageStoreSuffix(messageStoreInstanceSlot, ".group-v1-state-key");

    private static string GroupActivationKeySlot(string messageStoreInstanceSlot) =>
        ReplaceMessageStoreSuffix(
            messageStoreInstanceSlot,
            ".group-v1-invitation-activation-key");

    private static string ReplaceMessageStoreSuffix(string slot, string replacement)
    {
        const string suffix = ".message-store-instance";
        Assert.EndsWith(suffix, slot, StringComparison.Ordinal);
        return slot[..^suffix.Length] + replacement;
    }

    private static byte[] Bytes(int length, byte marker) =>
        Enumerable.Repeat(marker, length).ToArray();

    private sealed class RuntimeFixture : IDisposable
    {
        internal RuntimeFixture()
        {
            Directory = Path.Combine(
                Path.GetTempPath(),
                "deep-group-runtime-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
        }

        internal string Directory { get; }
        internal string GroupStatePath =>
            Path.Combine(Directory, "deep-store-v1", "groups.dgv1");
        internal string GroupActivationStatePath => Path.Combine(
            Directory,
            "deep-store-v1",
            "group-invitation-activation.gia1");
        internal string Xpk1JournalPath =>
            Path.Combine(Directory, "deep-store-v1", "xpk1-claims.xcj1");
        internal string DeviceStatePath =>
            Path.Combine(Directory, "deep-store-v1", "devices.dvs1");
        internal string[] GroupDatabaseFamily => DatabaseFamily(GroupStatePath);
        internal string[] GroupActivationDatabaseFamily =>
            DatabaseFamily(GroupActivationStatePath);

        internal DeepAccountRuntimeAccessor CreateAccessor() => new(
            Directory,
            new FrozenClock(DateTimeOffset.Parse("2026-09-08T00:00:00Z")),
            () => NetworkId.ToArray(),
            CreateStorage);

        internal async Task<byte[]?> ReadSecretAsync(string slot)
        {
            using var storage = CreateStorage(Directory);
            using var secret = await storage.ReadOwnedAsync(slot);
            if (secret is null)
            {
                return null;
            }
            var value = new byte[secret.Length];
            secret.CopyTo(value);
            return value;
        }

        internal async Task ReplaceSecretAsync(string slot, byte[] value)
        {
            using var storage = CreateStorage(Directory);
            await storage.DeleteBatchAsync([slot]);
            await storage.WriteBatchAsync([new DeepSecureStorageWrite(slot, value)]);
        }

        internal async Task DeleteSecretAsync(string slot)
        {
            using var storage = CreateStorage(Directory);
            await storage.DeleteBatchAsync([slot]);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }

        private static JournaledDeepSecureStorage CreateStorage(string root) => new(
            Path.Combine(root, "deep-store-v1", "secure-storage.dss"),
            new TestSecretProtector());

        private static string[] DatabaseFamily(string path) =>
            [path, path + "-wal", path + "-shm", path + "-journal"];
    }

    private sealed class TestSecretProtector : IDeepSecretProtector
    {
        private static readonly byte[] Key = SHA256.HashData("Deep/Test/GroupV1Runtime"u8);

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var output = new byte[4 + nonce.Length + plaintext.Length + 16];
            "TDS1"u8.CopyTo(output);
            nonce.CopyTo(output, 4);
            using var aes = new AesGcm(Key, 16);
            aes.Encrypt(nonce, plaintext, output.AsSpan(16, plaintext.Length),
                output.AsSpan(16 + plaintext.Length, 16), "Deep/Test/GroupV1Runtime/V1"u8);
            CryptographicOperations.ZeroMemory(nonce);
            return output;
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
        {
            if (protectedBytes.Length < 32 || !protectedBytes[..4].SequenceEqual("TDS1"u8))
            {
                throw new CryptographicException("Test secure-storage envelope is invalid.");
            }
            var output = new byte[protectedBytes.Length - 32];
            using var aes = new AesGcm(Key, 16);
            aes.Decrypt(protectedBytes.Slice(4, 12),
                protectedBytes.Slice(16, output.Length), protectedBytes[^16..], output,
                "Deep/Test/GroupV1Runtime/V1"u8);
            return output;
        }
    }

    private sealed class AuthenticatedTransport :
        ISessionMessageTransport,
        IAuthenticatedInboxTransport,
        IDirectP2pSessionMessageTransport,
        IMsg01AuthenticatedEvidenceSource,
        IDisposable
    {
        public Msg01VerifiedSessionAuthority EvidenceAuthority { get; } =
            Msg01VerifiedSessionAuthority.CreateTestEd25519(Bytes(32, 0x6A));

        public ValueTask<SessionId> GetLocalAccountAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<SessionId>(new InvalidOperationException("No legacy identity is active."));
        public ValueTask<DirectoryHeadHash32> ResolveFanoutTargetAsync(
            Msg01ResolveFanoutTargetRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<DirectoryHeadHash32>(new NotSupportedException());
        public ValueTask<Msg01PreparedTransportAttempt> PrepareAttemptAsync(
            Msg01PrepareAttemptRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<Msg01PreparedTransportAttempt>(new NotSupportedException());
        public ValueTask<Msg01AuthenticatedDispatchResult> DispatchPreparedAsync(
            Msg01DispatchEvidenceRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<Msg01AuthenticatedDispatchResult>(new NotSupportedException());
        public ValueTask<Msg01AuthenticatedDispatchResult> ReconcilePreparedAsync(
            Msg01DispatchEvidenceRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<Msg01AuthenticatedDispatchResult>(new NotSupportedException());
        public ValueTask<Msg01AuthenticatedInboundResult> GetInboundResultAsync(
            Msg01InboundEvidenceRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<Msg01AuthenticatedInboundResult>(new NotSupportedException());
        public ValueTask AcknowledgeReceiptAsync(
            ReadOnlyMemory<byte> authenticatedReceipt, CancellationToken cancellationToken) =>
            ValueTask.FromException(new NotSupportedException());
        public Task SendAsync(OutboundMessageEnvelope envelope,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<InboundMessageEnvelope>> ReceiveAsync(SessionId recipient,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InboundMessageEnvelope>>([]);
        public Task<IReadOnlyList<InboundMessageEnvelope>> ReceiveAuthenticatedAsync(
            SessionIdentityProvider identity, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InboundMessageEnvelope>>([]);
        public void Dispose() { }
    }
}
