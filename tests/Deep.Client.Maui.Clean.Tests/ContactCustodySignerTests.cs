using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Deep.Client.Maui.Services.GroupV1;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.ContactV1;
using Deep.Protocol.GroupV1;
using Sodium;

namespace Deep.Client.Maui.Clean.Tests;

public sealed class ContactCustodySignerTests
{
    [Fact]
    public async Task CurrentDeviceSignsContactAndGroupButRejectsForeignContactScope()
    {
        using var secrets = new InMemoryDeepSecureStorage();
        var network = Bytes(16, 0x11);
        var accounts = new DeepAccountService(
            new InMemoryDeepAccountStore(), secrets, new SystemClock(), network);
        var identity = (await accounts.CreateAsync("Alice")).Identity;
        var signer = new AccountOwnedDeviceCustodySigner(accounts, secrets, identity);
        var contact = (IContactDeviceCustodySigner)signer;
        var group = (IGroupDeviceCustodySigner)signer;

        var contactRequest = ContactRequest(
            contact, identity.Account.AccountIdentity.AccountId.Bytes.Span, network);
        var contactSignature = new byte[64];
        Assert.Equal(64, await contact.SignAsync(
            contactRequest, contactSignature, CancellationToken.None));
        Assert.True(PublicKeyAuth.VerifyDetached(
            contactSignature, contactRequest.SigningInput.ToArray(),
            identity.Device.SigningPublicKey.ToArray()));

        var groupRequest = CreateGroupSigningRequest(
            GroupDeviceSignaturePurpose.Proposal,
            network, Bytes(32, 0x31),
            identity.Account.AccountIdentity.AccountId.Bytes.Span,
            group.DeviceId.Span, group.CustodyDomainHash.Span,
            Bytes(96, 0x32));
        var groupSignature = new byte[64];
        Assert.Equal(64, await group.SignAsync(
            groupRequest, groupSignature, CancellationToken.None));
        Assert.True(PublicKeyAuth.VerifyDetached(
            groupSignature, groupRequest.SigningInput.ToArray(),
            identity.Device.SigningPublicKey.ToArray()));

        var foreign = ContactRequest(contact, Bytes(32, 0x41), network);
        var destination = Bytes(64, 0xa5);
        await Assert.ThrowsAsync<CryptographicException>(async () =>
            await contact.SignAsync(foreign, destination, CancellationToken.None));
        Assert.All(destination, static value => Assert.Equal((byte)0, value));

        var reopened = (IContactDeviceCustodySigner)new AccountOwnedDeviceCustodySigner(
            accounts, secrets, (await accounts.GetLocalIdentityAsync())!);
        var replaySignature = new byte[64];
        Assert.Equal(64, await reopened.SignAsync(
            contactRequest, replaySignature, CancellationToken.None));
        Assert.Equal(contactSignature, replaySignature);
        CryptographicOperations.ZeroMemory(contactSignature);
        CryptographicOperations.ZeroMemory(groupSignature);
        CryptographicOperations.ZeroMemory(replaySignature);
    }

    [Fact]
    public async Task MissingProtectedDeviceSeedFailsClosed()
    {
        using var secrets = new InMemoryDeepSecureStorage();
        var network = Bytes(16, 0x11);
        var accounts = new DeepAccountService(
            new InMemoryDeepAccountStore(), secrets, new SystemClock(), network);
        var identity = (await accounts.CreateAsync("Alice")).Identity;
        var signer = (IContactDeviceCustodySigner)new AccountOwnedDeviceCustodySigner(
            accounts, secrets, identity);
        var request = ContactRequest(
            signer, identity.Account.AccountIdentity.AccountId.Bytes.Span, network);
        await secrets.DeleteBatchAsync([identity.SecureSlots.DeviceSigningKey]);
        var destination = Bytes(64, 0xa5);

        await Assert.ThrowsAsync<LocalStateResetRequiredException>(async () =>
            await signer.SignAsync(request, destination, CancellationToken.None));
        Assert.All(destination, static value => Assert.Equal((byte)0, value));
    }

    private static ContactDeviceSigningRequest ContactRequest(
        IContactDeviceCustodySigner signer,
        ReadOnlySpan<byte> accountId,
        ReadOnlySpan<byte> network) =>
        CreateContactSigningRequest(
            ContactDeviceSignaturePurpose.RouteAdvertisement,
            network, accountId, signer.DeviceId.Span,
            signer.CustodyDomainHash.Span, Bytes(96, 0x42));

    private static byte[] Bytes(int length, byte value) =>
        Enumerable.Repeat(value, length).ToArray();

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern ContactDeviceSigningRequest CreateContactSigningRequest(
        ContactDeviceSignaturePurpose purpose,
        ReadOnlySpan<byte> networkId,
        ReadOnlySpan<byte> accountId,
        ReadOnlySpan<byte> deviceId,
        ReadOnlySpan<byte> custodyDomainHash,
        ReadOnlySpan<byte> signingInput);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern GroupDeviceSigningRequest CreateGroupSigningRequest(
        GroupDeviceSignaturePurpose purpose,
        ReadOnlySpan<byte> networkId,
        ReadOnlySpan<byte> groupId,
        ReadOnlySpan<byte> accountId,
        ReadOnlySpan<byte> deviceId,
        ReadOnlySpan<byte> custodyDomainHash,
        ReadOnlySpan<byte> signingInput);
}
