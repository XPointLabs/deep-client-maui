using System.Text;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

[Collection("SQLite global pool isolation")]
public sealed class ProductionMailboxContactStateStoreTests
{
    [Fact]
    public async Task InvitationsRoundTripPerAccountAndPeer()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        try
        {
            using var sqlite = new SqliteSessionStore(Path.Combine(root, "state.db"));
            var state = new ProductionMailboxContactStateStore(sqlite);
            var local = SessionId.CreateNew();
            var peer = SessionId.CreateNew();

            await state.SaveLocalInvitationAsync(local, "deep-contact-mailbox-invitation-v1:self");
            await state.SavePeerInvitationAsync(
                local, peer, "deep-contact-mailbox-invitation-v1:peer");

            Assert.Equal("deep-contact-mailbox-invitation-v1:self",
                await state.LoadLocalInvitationAsync(local));
            Assert.Equal("deep-contact-mailbox-invitation-v1:peer",
                await state.LoadPeerInvitationAsync(local, peer));
            Assert.Null(await state.LoadPeerInvitationAsync(local, SessionId.CreateNew()));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExactReplayIsIdempotentAndReplacementWins()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        try
        {
            using var sqlite = new SqliteSessionStore(Path.Combine(root, "state.db"));
            var state = new ProductionMailboxContactStateStore(sqlite);
            var local = SessionId.CreateNew();
            var peer = SessionId.CreateNew();

            await state.SavePeerInvitationAsync(local, peer, "first");
            await state.SavePeerInvitationAsync(local, peer, "first");
            await state.SavePeerInvitationAsync(local, peer, "second");

            Assert.Equal("second", await state.LoadPeerInvitationAsync(local, peer));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptValueFailsClosed()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        try
        {
            using var sqlite = new SqliteSessionStore(Path.Combine(root, "state.db"));
            var local = SessionId.CreateNew();
            var key = "deep.mailbox.contact-invitation.v1:" + local.Value + ":self";
            Assert.Equal(AtomicBoundedSettingMutationResult.Applied,
                await sqlite.CreateAtomicBoundedSettingAsync(
                    key, Encoding.UTF8.GetBytes("{\"Schema\":2,\"Invitation\":\"x\"}"), 2048));

            var state = new ProductionMailboxContactStateStore(sqlite);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => state.LoadLocalInvitationAsync(local));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Root() => Path.Combine(
        Path.GetTempPath(), "deep-production-contact-state-" + Guid.NewGuid().ToString("N"));
}
