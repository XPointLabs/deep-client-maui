using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Deep.Client.Maui.Services;
using Deep.Client.Maui.Services.GroupV1;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Domain.ContactV1;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Persistence.ContactV1;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.ContactV1;
using Deep.Protocol.ContactV1;
using Deep.Protocol.GroupV1;
using Deep.Protocol.ApplicationCore;
using Sodium;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class GroupV1CustodyCapabilityTests
{
    private static readonly byte[] NetworkId =
        Enumerable.Range(1, 16).Select(static value => (byte)value).ToArray();

    [Fact]
    public async Task CustodySigner_SignsWithCurrentAccountSlot_AfterRestart()
    {
        using var fixture = new CustodyFixture();
        DeepAccountCreationResult created;
        byte[] firstCustodyHash;

        await using (var first = fixture.CreateAccessor())
        {
            created = await CreateAccountAsync(await first.GetAccountsAsync());
            var signer = await first.TryGetGroupDeviceCustodySignerAsync();
            Assert.NotNull(signer);
            firstCustodyHash = signer!.CustodyDomainHash.ToArray();
            await AssertValidSignatureAsync(signer, created.Identity);
        }

        await using (var restarted = fixture.CreateAccessor())
        {
            var signer = await restarted.TryGetGroupDeviceCustodySignerAsync();
            Assert.NotNull(signer);
            Assert.Equal(firstCustodyHash, signer!.CustodyDomainHash.ToArray());
            Assert.Equal(
                created.Identity.Device.DeviceId.Bytes.ToArray(),
                signer.DeviceId.ToArray());
            Assert.Equal(
                created.Identity.Device.SigningPublicKey.ToArray(),
                signer.Ed25519PublicKey.ToArray());
            await AssertValidSignatureAsync(signer, created.Identity);
        }

        CryptographicOperations.ZeroMemory(firstCustodyHash);
    }

    [Fact]
    public async Task CustodySigner_WrongAccount_FailsClosedAndClearsDestination()
    {
        using var firstFixture = new CustodyFixture();
        using var secondFixture = new CustodyFixture();
        await using var firstAccessor = firstFixture.CreateAccessor();
        await using var secondAccessor = secondFixture.CreateAccessor();
        var firstIdentity = (await CreateAccountAsync(await firstAccessor.GetAccountsAsync()))
            .Identity;
        _ = await CreateAccountAsync(await secondAccessor.GetAccountsAsync());

        using var firstStorage = firstFixture.CreateStorage();
        var signer = new AccountOwnedGroupDeviceCustodySigner(
            await secondAccessor.GetAccountsAsync(),
            firstStorage,
            firstIdentity);
        var request = CreateRequest(signer, firstIdentity);
        var destination = Enumerable.Repeat((byte)0xa5, 64).ToArray();

        await Assert.ThrowsAsync<CryptographicException>(async () =>
            await signer.SignAsync(request, destination, CancellationToken.None));
        Assert.All(destination, static value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public async Task CustodySigner_RequestForAnotherAccount_FailsBeforeSecureRead()
    {
        using var fixture = new CustodyFixture();
        await using var accessor = fixture.CreateAccessor();
        var identity = (await CreateAccountAsync(await accessor.GetAccountsAsync())).Identity;
        var signer = await accessor.TryGetGroupDeviceCustodySignerAsync();
        Assert.NotNull(signer);
        var wrongAccount = identity.Account.AccountIdentity.AccountId.Bytes.ToArray();
        wrongAccount[0] ^= 0x80;
        var request = CreateSigningRequest(
            GroupDeviceSignaturePurpose.Proposal,
            identity.NetworkId.Span,
            Bytes(32, 0x41),
            wrongAccount,
            signer!.DeviceId.Span,
            signer.CustodyDomainHash.Span,
            Bytes(96, 0x52));
        var destination = Enumerable.Repeat((byte)0xa5, 64).ToArray();

        await Assert.ThrowsAsync<CryptographicException>(async () =>
            await signer.SignAsync(request, destination, CancellationToken.None));
        Assert.All(destination, static value => Assert.Equal((byte)0, value));
        CryptographicOperations.ZeroMemory(wrongAccount);
    }

    [Fact]
    public async Task CustodySigner_ReplacedSigningSeed_FailsClosed()
    {
        using var fixture = new CustodyFixture();
        DeepLocalIdentitySnapshot identity;
        await using (var first = fixture.CreateAccessor())
        {
            identity = (await CreateAccountAsync(await first.GetAccountsAsync())).Identity;
        }

        var replacement = Bytes(32, 0x7c);
        try
        {
            using var storage = fixture.CreateStorage();
            await storage.DeleteBatchAsync([identity.SecureSlots.DeviceSigningKey]);
            await storage.WriteBatchAsync(
                [new DeepSecureStorageWrite(identity.SecureSlots.DeviceSigningKey, replacement)]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(replacement);
        }

        await using var restarted = fixture.CreateAccessor();
        await Assert.ThrowsAsync<LocalStateResetRequiredException>(async () =>
            await restarted.TryGetGroupDeviceCustodySignerAsync());
    }

    [Fact]
    public void ContactCapabilityBinding_AcceptsOwnerAndParticipantExactBindings()
    {
        var owner = Bytes(32, 0x21);
        var participant = Bytes(32, 0x31);

        ProductionGroupContactCapabilitySource.ValidateVerifiedBinding(
            GroupContactCapabilityRole.Owner,
            NetworkId,
            owner,
            owner,
            ContactAddressKind.PermanentDeepId,
            NetworkId,
            owner,
            hasOneTimeClaimReceipt: false);
        ProductionGroupContactCapabilitySource.ValidateVerifiedBinding(
            GroupContactCapabilityRole.Participant,
            NetworkId,
            owner,
            participant,
            ContactAddressKind.OneTimeInvitation,
            NetworkId,
            participant,
            hasOneTimeClaimReceipt: true);
    }

    [Fact]
    public async Task ContactCapabilitySource_WrongNetwork_FailsBeforeProtocolVerifier()
    {
        await using var runtime = new DeepAccountTestRuntime();
        var identity = (await CreateAccountAsync(runtime.Accounts)).Identity;
        var verifier = new CountingUnavailableContactCapabilitySource();
        var source = new ProductionGroupContactCapabilitySource(runtime.Accounts, verifier);
        var wrongNetwork = identity.NetworkId.ToArray();
        wrongNetwork[0] ^= 0x80;
        var input = CreateUnavailablePermanentInput(wrongNetwork);

        await Assert.ThrowsAsync<CryptographicException>(async () =>
            await source.ReverifyOwnerAsync(identity, input));
        Assert.Equal(0, verifier.Calls);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ContactCapabilityBinding_RejectsWrongNetworkAccountOrClaimSemantics(
        bool wrongNetwork,
        bool wrongAccount,
        bool wrongClaimSemantics)
    {
        var owner = Bytes(32, 0x21);
        var participant = Bytes(32, 0x31);
        var verifiedNetwork = NetworkId.ToArray();
        var verifiedAccount = participant.ToArray();
        if (wrongNetwork)
        {
            verifiedNetwork[0] ^= 0x80;
        }
        if (wrongAccount)
        {
            verifiedAccount[0] ^= 0x80;
        }

        Assert.Throws<CryptographicException>(() =>
            ProductionGroupContactCapabilitySource.ValidateVerifiedBinding(
                GroupContactCapabilityRole.Participant,
                NetworkId,
                owner,
                participant,
                ContactAddressKind.OneTimeInvitation,
                verifiedNetwork,
                verifiedAccount,
                hasOneTimeClaimReceipt: !wrongClaimSemantics));
    }

    [Fact]
    public void DurableContactEvidence_CannotCurrentlyRecreateVerifierInputPublicly()
    {
        Assert.Empty(typeof(ContactResolverVerificationInput).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public));
        Assert.NotNull(typeof(ContactResolveOperationSnapshot).GetProperty(
            nameof(ContactResolveOperationSnapshot.ExactXiq1)));
        Assert.NotNull(typeof(ContactResolveOperationSnapshot).GetProperty(
            nameof(ContactResolveOperationSnapshot.ExactXis1)));
    }

    private static async Task AssertValidSignatureAsync(
        IGroupDeviceCustodySigner signer,
        DeepLocalIdentitySnapshot identity)
    {
        var request = CreateRequest(signer, identity);
        var signature = new byte[64];
        var written = await signer.SignAsync(request, signature, CancellationToken.None);

        Assert.Equal(64, written);
        Assert.True(PublicKeyAuth.VerifyDetached(
            signature,
            request.SigningInput.ToArray(),
            signer.Ed25519PublicKey.ToArray()));
        CryptographicOperations.ZeroMemory(signature);
    }

    private static GroupDeviceSigningRequest CreateRequest(
        IGroupDeviceCustodySigner signer,
        DeepLocalIdentitySnapshot identity) =>
        CreateSigningRequest(
            GroupDeviceSignaturePurpose.Proposal,
            identity.NetworkId.Span,
            Bytes(32, 0x41),
            identity.Account.AccountIdentity.AccountId.Bytes.Span,
            signer.DeviceId.Span,
            signer.CustodyDomainHash.Span,
            Bytes(96, 0x52));

    private static ContactResolverVerificationInput CreateUnavailablePermanentInput(
        ReadOnlySpan<byte> networkId)
    {
        var did = ApplicationCoreCodec.AuthorDid1(Bytes(32, 0x61), Bytes(16, 0x62));
        var address = new ImportedContactAddress(
            ContactAddressKind.PermanentDeepId,
            networkId,
            did.CanonicalBytes.Span,
            did.Text,
            expiresAtUnixSeconds: null);
        var request = Xiq1Codec.Encode(
            networkId,
            Bytes(32, 0x63),
            Bytes(32, 0x64),
            Bytes(32, 0x65),
            issuedAt: 10,
            expiresAt: 100,
            Bytes(32, 0x66),
            requestedGeneration: 0,
            Xiq1AntiSpamTokenType.None,
            ReadOnlySpan<byte>.Empty,
            ContactServicePaddingClass.Bytes256);
        var result = Xis1Codec.Encode(
            request,
            Xis1Status.NotFound,
            ContactServiceMutationOutcome.None,
            serverTime: 50,
            retryAfter: 0,
            ContactServicePaddingClass.Bytes256,
            Array.Empty<ReadOnlyMemory<byte>>());
        return new ContactResolverVerificationInput(
            address,
            Xiq1Codec.Decode(request),
            Xis1Codec.Decode(result, request));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern GroupDeviceSigningRequest CreateSigningRequest(
        GroupDeviceSignaturePurpose purpose,
        ReadOnlySpan<byte> networkId,
        ReadOnlySpan<byte> groupId,
        ReadOnlySpan<byte> accountId,
        ReadOnlySpan<byte> deviceId,
        ReadOnlySpan<byte> custodyDomainHash,
        ReadOnlySpan<byte> signingInput);

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

    private static byte[] Bytes(int length, byte marker) =>
        Enumerable.Repeat(marker, length).ToArray();

    private sealed class CustodyFixture : IDisposable
    {
        internal CustodyFixture()
        {
            Directory = Path.Combine(
                Path.GetTempPath(),
                "deep-group-custody-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
        }

        private string Directory { get; }

        internal DeepAccountRuntimeAccessor CreateAccessor() => new(
            Directory,
            new FrozenClock(DateTimeOffset.Parse("2026-09-08T00:00:00Z")),
            () => NetworkId.ToArray(),
            CreateStorage,
            static () => new TestSecretProtector());

        internal JournaledDeepSecureStorage CreateStorage() => CreateStorage(Directory);

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
    }

    private sealed class CountingUnavailableContactCapabilitySource
        : IContactResolverVerifiedCapabilitySource
    {
        internal int Calls { get; private set; }

        public ValueTask<ContactResolverVerifiedCapabilitySet> VerifyAsync(
            ContactResolverVerificationInput input,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromException<ContactResolverVerifiedCapabilitySet>(
                new CryptographicException("Verifier must not be reached."));
        }
    }

    private sealed class TestSecretProtector : IDeepSecretProtector
    {
        private static readonly byte[] Key =
            SHA256.HashData("Deep/Test/GroupV1/Custody"u8);

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var output = new byte[4 + nonce.Length + plaintext.Length + 16];
            "TGC1"u8.CopyTo(output);
            nonce.CopyTo(output, 4);
            using var aes = new AesGcm(Key, 16);
            aes.Encrypt(
                nonce,
                plaintext,
                output.AsSpan(16, plaintext.Length),
                output.AsSpan(16 + plaintext.Length, 16),
                "Deep/Test/GroupV1/Custody/V1"u8);
            CryptographicOperations.ZeroMemory(nonce);
            return output;
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
        {
            if (protectedBytes.Length < 32 || !protectedBytes[..4].SequenceEqual("TGC1"u8))
            {
                throw new CryptographicException("Test secure-storage envelope is invalid.");
            }
            var output = new byte[protectedBytes.Length - 32];
            using var aes = new AesGcm(Key, 16);
            aes.Decrypt(
                protectedBytes.Slice(4, 12),
                protectedBytes.Slice(16, output.Length),
                protectedBytes[^16..],
                output,
                "Deep/Test/GroupV1/Custody/V1"u8);
            return output;
        }
    }
}
