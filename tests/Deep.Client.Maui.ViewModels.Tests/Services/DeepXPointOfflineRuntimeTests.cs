using System.Buffers.Binary;
using System.Security.Cryptography;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Persistence.XPointNetworkV1;
using Deep.Client.Shared.Services;
using Deep.Protocol.XPointNetworkV1;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class DeepXPointOfflineRuntimeTests
{
    private static readonly byte[] NetworkId = Enumerable.Range(1, 16)
        .Select(static value => (byte)value)
        .ToArray();

    [Fact]
    public async Task StoresOpenLazilyOfflineWithDistinctProtectedKeys()
    {
        using var fixture = new RuntimeFixture();
        await using var accessor = fixture.CreateAccessor();
        var created = await CreateAccountAsync(await accessor.GetAccountsAsync());
        var networkSlot = NetworkKeySlot(created.Identity.SecureSlots.MessageStoreInstanceId);
        var guardSlot = GuardKeySlot(created.Identity.SecureSlots.MessageStoreInstanceId);

        Assert.DoesNotContain(fixture.XPointDatabaseFamily, File.Exists);
        Assert.DoesNotContain(fixture.GuardDatabaseFamily, File.Exists);
        Assert.Null(await fixture.ReadSecretAsync(networkSlot));
        Assert.Null(await fixture.ReadSecretAsync(guardSlot));

        var networkStore = await accessor.GetXPointNetworkStateStoreAsync();

        Assert.NotNull(networkStore);
        Assert.True(File.Exists(fixture.XPointStatePath));
        Assert.True(File.Exists(fixture.GuardStatePath));
        var networkKey = Assert.IsType<byte[]>(await fixture.ReadSecretAsync(networkSlot));
        var guardKey = Assert.IsType<byte[]>(await fixture.ReadSecretAsync(guardSlot));
        try
        {
            Assert.Equal(32, networkKey.Length);
            Assert.Equal(32, guardKey.Length);
            Assert.Contains(networkKey, static value => value != 0);
            Assert.Contains(guardKey, static value => value != 0);
            Assert.False(CryptographicOperations.FixedTimeEquals(networkKey, guardKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(networkKey);
            CryptographicOperations.ZeroMemory(guardKey);
        }

        Assert.NotNull(await accessor.GetProtectedEntryGuardStoreAsync());
        Assert.Equal(1, fixture.NetworkIdReads);
    }

    [Fact]
    public async Task StoresPersistAcrossOfflineRestartAndDisposeWithTheirOwner()
    {
        using var fixture = new RuntimeFixture();
        IXPointNetworkStateStore networkStore;
        IProtectedEntryGuardStore guardStore;

        await using (var first = fixture.CreateAccessor())
        {
            await CreateAccountAsync(await first.GetAccountsAsync());
            networkStore = await first.GetXPointNetworkStateStoreAsync();
            guardStore = await first.GetProtectedEntryGuardStoreAsync();
            Assert.Equal(
                XPointNetworkStoreWriteDisposition.Applied,
                (await networkStore.CompareExchangeAsync(
                    null,
                    NetworkSnapshot(),
                    CancellationToken.None)).Disposition);
            Assert.Equal(
                EntryGuardStoreWriteDisposition.Applied,
                (await guardStore.CompareExchangeAsync(
                    null,
                    GuardState(),
                    CancellationToken.None)).Disposition);
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            networkStore.ReadAsync(CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            guardStore.ReadAsync(CancellationToken.None).AsTask());

        await using (var reopened = fixture.CreateAccessor())
        {
            var restoredNetwork = Assert.IsType<XPointNetworkStateSnapshot>(
                await (await reopened.GetXPointNetworkStateStoreAsync())
                    .ReadAsync(CancellationToken.None));
            var restoredGuards = Assert.IsType<EntryGuardState>(
                await (await reopened.GetProtectedEntryGuardStoreAsync())
                    .ReadAsync(CancellationToken.None));

            Assert.Equal(1UL, restoredNetwork.Revision);
            Assert.Equal(7UL, restoredNetwork.ProtectedLkg.ViewGeneration);
            Assert.Equal(1UL, restoredGuards.Revision);
            Assert.Equal(7UL, restoredGuards.ViewGeneration);
        }

        Assert.Equal(2, fixture.NetworkIdReads);
    }

    [Fact]
    public async Task ResetClosesStoresAndPurgesBothDatabaseFamiliesAndKeySlots()
    {
        using var fixture = new RuntimeFixture();
        await using var accessor = fixture.CreateAccessor();
        var created = await CreateAccountAsync(await accessor.GetAccountsAsync());
        var networkSlot = NetworkKeySlot(created.Identity.SecureSlots.MessageStoreInstanceId);
        var guardSlot = GuardKeySlot(created.Identity.SecureSlots.MessageStoreInstanceId);
        var networkStore = await accessor.GetXPointNetworkStateStoreAsync();
        var guardStore = await accessor.GetProtectedEntryGuardStoreAsync();

        Assert.True(File.Exists(fixture.XPointStatePath));
        Assert.True(File.Exists(fixture.GuardStatePath));
        Assert.NotNull(await fixture.ReadSecretAsync(networkSlot));
        Assert.NotNull(await fixture.ReadSecretAsync(guardSlot));

        await accessor.ResetLocalStateAsync();

        Assert.DoesNotContain(fixture.XPointDatabaseFamily, File.Exists);
        Assert.DoesNotContain(fixture.GuardDatabaseFamily, File.Exists);
        Assert.Null(await fixture.ReadSecretAsync(networkSlot));
        Assert.Null(await fixture.ReadSecretAsync(guardSlot));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            networkStore.ReadAsync(CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            guardStore.ReadAsync(CancellationToken.None).AsTask());
        Assert.Null(await (await accessor.GetAccountsAsync()).GetLocalIdentityAsync());
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

    private static XPointNetworkStateSnapshot NetworkSnapshot() => new(
        1,
        new XPointNetworkProtectedLkg(
            NetworkId,
            Reference("XNH1", 0x21),
            8,
            Bytes(32, 0x31),
            Reference("XNV1", 0x41),
            7,
            Reference("XNA1", 0x51)),
        forkLatched: false);

    private static EntryGuardState GuardState() => new(
        1,
        NetworkId,
        7,
        Bytes(32, 0x41),
        Bytes(32, 0x51),
        Bytes(32, 0x61),
        [(ReadOnlyMemory<byte>)Bytes(32, 0x61)]);

    private static byte[] Reference(string magic, byte marker)
    {
        var value = new byte[38];
        System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(value, 0);
        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(4), 1);
        Bytes(32, marker).CopyTo(value, 6);
        return value;
    }

    private static byte[] Bytes(int length, byte marker) => Enumerable.Range(0, length)
        .Select(index => checked((byte)(marker + index)))
        .ToArray();

    private static string NetworkKeySlot(string messageStoreInstanceSlot) =>
        ReplaceMessageStoreSuffix(messageStoreInstanceSlot, ".xpoint-network-state-key");

    private static string GuardKeySlot(string messageStoreInstanceSlot) =>
        ReplaceMessageStoreSuffix(messageStoreInstanceSlot, ".xpoint-entry-guard-key");

    private static string ReplaceMessageStoreSuffix(string slot, string replacement)
    {
        const string suffix = ".message-store-instance";
        Assert.EndsWith(suffix, slot, StringComparison.Ordinal);
        return slot[..^suffix.Length] + replacement;
    }

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(),
            "deep-xpoint-maui-" + Guid.NewGuid().ToString("N"));

        internal RuntimeFixture() => Directory.CreateDirectory(directory);

        internal int NetworkIdReads { get; private set; }
        internal string XPointStatePath =>
            Path.Combine(directory, "deep-store-v1", "xpoint-network.xlk1");
        internal string GuardStatePath =>
            Path.Combine(directory, "deep-store-v1", "entry-guards.xgs1");
        internal string[] XPointDatabaseFamily => DatabaseFamily(XPointStatePath);
        internal string[] GuardDatabaseFamily => DatabaseFamily(GuardStatePath);

        internal DeepAccountRuntimeAccessor CreateAccessor() => new(
            directory,
            new FrozenClock(DateTimeOffset.Parse("2026-09-07T00:00:00Z")),
            () =>
            {
                NetworkIdReads++;
                return NetworkId.ToArray();
            },
            CreateStorage);

        internal async Task<byte[]?> ReadSecretAsync(string slot)
        {
            using var storage = CreateStorage(directory);
            using var secret = await storage.ReadOwnedAsync(slot);
            if (secret is null)
            {
                return null;
            }
            var value = new byte[secret.Length];
            secret.CopyTo(value);
            return value;
        }

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
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
        private static readonly byte[] Key = SHA256.HashData("Deep/Test/XPointRuntime"u8);

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            if (plaintext.IsEmpty)
            {
                throw new ArgumentException("Plaintext is required.", nameof(plaintext));
            }
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
                "Deep/Test/XPointRuntime/V1"u8);
            CryptographicOperations.ZeroMemory(nonce);
            return output;
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
        {
            if (protectedBytes.Length < 32 || !protectedBytes[..4].SequenceEqual("TDS1"u8))
            {
                throw new CryptographicException("Test secure-storage envelope is invalid.");
            }
            var plaintextLength = protectedBytes.Length - 32;
            var output = new byte[plaintextLength];
            using var aes = new AesGcm(Key, 16);
            aes.Decrypt(
                protectedBytes.Slice(4, 12),
                protectedBytes.Slice(16, plaintextLength),
                protectedBytes[^16..],
                output,
                "Deep/Test/XPointRuntime/V1"u8);
            return output;
        }
    }
}
