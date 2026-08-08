using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

[Collection("Secure recovery identity isolation")]
public sealed class ProductionMailboxRuntimeProvisioningSourceTests : IDisposable
{
    private const string Phrase =
        "amaze buffet cake entrance symptoms tiger lamb maze nestle python dusted faxed faxed";

    public ProductionMailboxRuntimeProvisioningSourceTests() =>
        ProductionMailboxOwnerIdentityStore.RemoveAsync().GetAwaiter().GetResult();

    [Theory]
    [InlineData(MailboxInfrastructureOwnership.DirectP2p)]
    [InlineData(MailboxInfrastructureOwnership.UserManaged)]
    public async Task ProvisionAsync_RejectsNonOfficialOwnershipBeforeAcquisition(
        MailboxInfrastructureOwnership ownership)
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"deep-production-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var sqlite = new SqliteSessionStore(new SqliteSessionStoreOptions(
                Path.Combine(directory, "client-state.db"), new string('A', 64)));
            using var secureStore = new SecureRecoverySessionStore(sqlite);
            using var identity = new SessionIdentityProvider(
                "amaze buffet cake entrance symptoms tiger lamb maze nestle python dusted faxed faxed");
            var holderKey = identity.GetEd25519PublicKey();
            try
            {
                var acquirer = new RejectingAcquirer();
                var source = new ProductionMailboxRuntimeProvisioningSource(
                    secureStore,
                    acquirer,
                    Anchor(),
                    new RejectingTrustStore(),
                    new ProductionMailboxClientApprovalIdentity(
                        MailboxClientPlatform.Android,
                        ProductionMailboxControlPlaneVerifier.AndroidApplicationIdentity,
                        Fill(32, 0x31),
                        Fill(32, 0x32)));

                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    source.ProvisionAsync(
                        sqlite,
                        new MailboxHolderIdentity(identity.SessionId, holderKey),
                        ownership));

                Assert.Equal(
                    "Production Registry provisioning is official-managed only.",
                    error.Message);
                Assert.Equal(0, acquirer.Calls);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(holderKey);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptActiveBundle_FailsClosedWithoutRegistryAcquisition()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"deep-production-source-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var sqlite = new SqliteSessionStore(new SqliteSessionStoreOptions(
                Path.Combine(directory, "client-state.db"), new string('A', 64)));
            using var secureStore = new SecureRecoverySessionStore(sqlite);
            using var identity = new SessionIdentityProvider(Phrase);
            var account = new SessionAccount(
                identity.SessionId, "Mr. X", DateTimeOffset.Parse("2026-08-08T00:00:00Z"));
            await secureStore.SetAsync(
                SessionAccountService.ActiveRecoveryPhraseKey, Phrase);
            await secureStore.SetAsync(SessionAccountService.ActiveAccountKey, account);
            await sqlite.SetAsync(
                "deep.mailbox.production-local-owner.v1:" + identity.SessionId.Value +
                ":active-public-bundle-v1",
                new { corrupt = true });
            var holderKey = identity.GetEd25519PublicKey();
            try
            {
                var acquirer = new RejectingAcquirer();
                var source = new ProductionMailboxRuntimeProvisioningSource(
                    secureStore,
                    acquirer,
                    Anchor(),
                    new RejectingTrustStore(),
                    new ProductionMailboxClientApprovalIdentity(
                        MailboxClientPlatform.Android,
                        ProductionMailboxControlPlaneVerifier.AndroidApplicationIdentity,
                        Fill(32, 0x31),
                        Fill(32, 0x32)));

                var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                    source.ProvisionAsync(
                        sqlite,
                        new MailboxHolderIdentity(identity.SessionId, holderKey),
                        MailboxInfrastructureOwnership.OfficialManaged));

                Assert.Contains("bundle is corrupt", error.Message,
                    StringComparison.Ordinal);
                Assert.Equal(0, acquirer.Calls);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(holderKey);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    public void Dispose() =>
        ProductionMailboxOwnerIdentityStore.RemoveAsync().GetAwaiter().GetResult();

    private static ProductionMailboxTrustAnchor Anchor() => new(
        Fill(32, 0x11), Fill(16, 0x12), 1, Fill(32, 0x13), 1,
        Fill(32, 0x14), Fill(32, 0x15), 1, Fill(32, 0x16));

    private static byte[] Fill(int length, byte value) =>
        Enumerable.Repeat(value, length).ToArray();

    private sealed class RejectingAcquirer : IProductionMailboxLocalOwnerAcquirer
    {
        public int Calls { get; private set; }

        public Task<ProductionMailboxLocalOwnerBundle> AcquireAsync(
            SessionIdentityProvider holder,
            ProductionMailboxOwnerIdentity owner,
            ProductionMailboxClientApprovalIdentity clientIdentity,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Acquisition must not run.");
        }
    }

    private sealed class RejectingTrustStore : IProductionMailboxTrustStateStore
    {
        public Task<ProductionMailboxTrustState?> ReadAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Trust state must not be read.");

        public Task CommitAsync(
            ulong expectedRevision,
            ProductionMailboxTrustState replacement,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Trust state must not be written.");
    }
}
