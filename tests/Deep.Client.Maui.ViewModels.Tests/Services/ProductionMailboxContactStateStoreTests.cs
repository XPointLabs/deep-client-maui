using System.Text;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.MailboxCapabilities;
using Deep.Protocol.DeepExtension.MailboxTopology;
using Sodium;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

[Collection("SQLite global pool isolation")]
public sealed class ProductionMailboxContactStateStoreTests
{
    private const string PeerPhrase =
        "amaze buffet cake entrance symptoms tiger lamb maze nestle python dusted faxed faxed";
    private const ulong Now = 1_800_000_000;

    [Fact]
    public async Task ExactReplayIsIdempotent()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        try
        {
            using var sqlite = new SqliteSessionStore(Path.Combine(root, "state.db"));
            var state = Store(sqlite);
            var local = SessionId.CreateNew();
            var invitation = CreateInvitation(routeSequence: 7);

            await state.SavePeerInvitationAsync(local, invitation.Peer, invitation.Text);
            await state.SavePeerInvitationAsync(local, invitation.Peer, invitation.Text);

            Assert.Equal(
                invitation.Text,
                await state.LoadPeerInvitationAsync(local, invitation.Peer));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task HigherRouteSequenceReplacesAndLowerSequenceIsRejected()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        try
        {
            using var sqlite = new SqliteSessionStore(Path.Combine(root, "state.db"));
            var state = Store(sqlite);
            var local = SessionId.CreateNew();
            var first = CreateInvitation(routeSequence: 7);
            var successor = CreateInvitation(routeSequence: 8);

            await state.SavePeerInvitationAsync(local, first.Peer, first.Text);
            await state.SavePeerInvitationAsync(local, successor.Peer, successor.Text);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                state.SavePeerInvitationAsync(local, first.Peer, first.Text));
            Assert.Equal(
                successor.Text,
                await state.LoadPeerInvitationAsync(local, successor.Peer));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task SameRouteSequenceWithDifferentCanonicalInvitationIsRejectedAsEquivocation()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        try
        {
            using var sqlite = new SqliteSessionStore(Path.Combine(root, "state.db"));
            var state = Store(sqlite);
            var local = SessionId.CreateNew();
            var accepted = CreateInvitation(routeSequence: 7, routeVariant: 0);
            var equivocation = CreateInvitation(routeSequence: 7, routeVariant: 1);

            await state.SavePeerInvitationAsync(local, accepted.Peer, accepted.Text);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                state.SavePeerInvitationAsync(local, equivocation.Peer, equivocation.Text));
            Assert.Equal(
                accepted.Text,
                await state.LoadPeerInvitationAsync(local, accepted.Peer));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task SchemaTwoRecordSurvivesRestart()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(root, "state.db");
            var local = SessionId.CreateNew();
            var invitation = CreateInvitation(routeSequence: 19);
            using (var sqlite = new SqliteSessionStore(database))
            {
                await Store(sqlite).SaveLocalInvitationAsync(local, invitation.Text);
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using var reopened = new SqliteSessionStore(database);
            Assert.Equal(
                invitation.Text,
                await Store(reopened).LoadLocalInvitationAsync(local));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task CorruptVerifiedMetadataFailsClosed()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        try
        {
            using var sqlite = new SqliteSessionStore(Path.Combine(root, "state.db"));
            var local = SessionId.CreateNew();
            var invitation = CreateInvitation(routeSequence: 7);
            var state = Store(sqlite);
            await state.SaveLocalInvitationAsync(local, invitation.Text);

            var key = Key(local, null);
            var stored = await sqlite.ReadAtomicBoundedSettingAsync(key, 2048);
            var corrupt = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(stored.GetValueCopy())
                    .Replace("\"RouteSequence\":7", "\"RouteSequence\":8", StringComparison.Ordinal));
            Assert.Equal(
                AtomicBoundedSettingMutationResult.Applied,
                await sqlite.ReplaceAtomicBoundedSettingAsync(
                    key, stored.Revision!, corrupt, 2048));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => state.LoadLocalInvitationAsync(local));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task OversizedPersistedRecordFailsClosed()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        try
        {
            using var sqlite = new SqliteSessionStore(Path.Combine(root, "state.db"));
            var local = SessionId.CreateNew();
            var oversized = Encoding.UTF8.GetBytes(
                "{\"Padding\":\"" + new string('a', 2048) + "\"}");
            Assert.Equal(
                AtomicBoundedSettingMutationResult.Applied,
                await sqlite.CreateAtomicBoundedSettingAsync(
                    Key(local, null), oversized, 4096));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => Store(sqlite).LoadLocalInvitationAsync(local));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task LegacySchemaOneFailsClosed()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        try
        {
            using var sqlite = new SqliteSessionStore(Path.Combine(root, "state.db"));
            var local = SessionId.CreateNew();
            var invitation = CreateInvitation(routeSequence: 7);
            var legacy = Encoding.UTF8.GetBytes(
                "{\"Schema\":1,\"Invitation\":\"" + invitation.Text + "\"}");
            Assert.Equal(
                AtomicBoundedSettingMutationResult.Applied,
                await sqlite.CreateAtomicBoundedSettingAsync(
                    Key(local, null), legacy, 2048));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => Store(sqlite).LoadLocalInvitationAsync(local));
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static ProductionMailboxContactStateStore Store(
        IAtomicBoundedSettingsRepository settings) =>
        new(settings, new FixedTimeProvider(Now));

    private static InvitationFixture CreateInvitation(
        ulong routeSequence,
        byte routeVariant = 0)
    {
        using var peer = new SessionIdentityProvider(PeerPhrase);
        var route = CreateRoute(routeSequence, routeVariant);
        return new InvitationFixture(
            peer.SessionId,
            ContactMailboxInvitationService.Create(
                peer,
                route.Owner.PublicKey,
                route.CanonicalAdvertisement,
                DateTimeOffset.FromUnixTimeSeconds(checked((long)(Now + 1_800))),
                new FixedTimeProvider(Now)));
    }

    private static RouteFixture CreateRoute(ulong routeSequence, byte routeVariant)
    {
        var issuer = PublicKeyAuth.GenerateKeyPair(Bytes(0x21, 32));
        var owner = PublicKeyAuth.GenerateKeyPair(Bytes(0x51, 32));
        var placement = Bytes(unchecked((byte)(0x81 + routeVariant)), 32);
        var certificate = new ProductionMailboxRouteCertificate
        {
            NetworkId = Bytes(0x01, 16),
            AuthorityGeneration = 4,
            CanonicalAuthorityHash = Bytes(0x11, 32),
            IssuerEd25519PublicKey = issuer.PublicKey,
            MailboxOwnerEd25519PublicKey = owner.PublicKey,
            BlindedMailboxId = Bytes(unchecked((byte)(0x61 + routeVariant)), 32),
            BlindedPlacementId = placement,
            SelectionInputCommitment = ProductionMailboxReplicaSelection
                .ComputeSelectionInputCommitment(new BlindedPlacementId(placement)),
            IssuedAtUnixSeconds = Now - 120,
            ExpiresAtUnixSeconds = Now + 3_600,
            IssuerSignature = new byte[64]
        };
        certificate = certificate with
        {
            IssuerSignature = PublicKeyAuth.SignDetached(
                ProductionMailboxRouteAdvertisementCodec.GetCertificateSigningBytes(certificate),
                issuer.PrivateKey)
        };
        var advertisement = new ProductionMailboxRouteAdvertisement
        {
            Certificate = certificate,
            Sequence = routeSequence,
            PublishedAtUnixSeconds = Now - 60,
            ExpiresAtUnixSeconds = Now + 3_600,
            OwnerSignature = new byte[64]
        };
        advertisement = advertisement with
        {
            OwnerSignature = PublicKeyAuth.SignDetached(
                ProductionMailboxRouteAdvertisementCodec.GetAdvertisementSigningBytes(advertisement),
                owner.PrivateKey)
        };
        return new RouteFixture(
            owner,
            ProductionMailboxRouteAdvertisementCodec.EncodeAdvertisement(advertisement));
    }

    private static string Key(SessionId local, SessionId? peer) =>
        "deep.mailbox.contact-invitation.v1:" + local.Value + ":" +
        (peer?.Value ?? "self");

    private static byte[] Bytes(byte seed, int length) => Enumerable.Range(0, length)
        .Select(index => unchecked((byte)(seed + index)))
        .ToArray();

    private static string Root() => Path.Combine(
        Path.GetTempPath(), "deep-production-contact-state-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string root)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    private sealed class FixedTimeProvider(ulong now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.FromUnixTimeSeconds(checked((long)now));
    }

    private sealed record RouteFixture(KeyPair Owner, byte[] CanonicalAdvertisement);
    private sealed record InvitationFixture(SessionId Peer, string Text);
}
