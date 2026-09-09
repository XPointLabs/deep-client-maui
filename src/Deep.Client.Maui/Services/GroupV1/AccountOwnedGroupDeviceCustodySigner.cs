using System.Buffers.Binary;
using System.Security.Cryptography;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Protocol.GroupV1;
using Deep.Protocol.Identity;
using Sodium;

namespace Deep.Client.Maui.Services.GroupV1;

/// <summary>
/// Account-owned Group v1 signing boundary. The Ed25519 seed is read only from
/// the reconciled current-device secure slot for the duration of one signature.
/// </summary>
internal sealed class AccountOwnedGroupDeviceCustodySigner : IGroupDeviceCustodySigner
{
    private static ReadOnlySpan<byte> CustodyDomain =>
        "Deep/Client/MAUI/GroupV1/device-custody-domain/v1"u8;

    private readonly DeepAccountService accounts;
    private readonly IDeepSecureStorage secureStorage;
    private readonly DeepLocalIdentitySnapshot expectedIdentity;
    private readonly byte[] networkId;
    private readonly byte[] accountId;
    private readonly byte[] deviceId;
    private readonly byte[] publicKey;
    private readonly byte[] custodyDomainHash;

    internal AccountOwnedGroupDeviceCustodySigner(
        DeepAccountService accounts,
        IDeepSecureStorage secureStorage,
        DeepLocalIdentitySnapshot expectedIdentity)
    {
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.secureStorage = secureStorage
            ?? throw new ArgumentNullException(nameof(secureStorage));
        this.expectedIdentity = expectedIdentity
            ?? throw new ArgumentNullException(nameof(expectedIdentity));

        networkId = ExactNonzero(expectedIdentity.NetworkId.Span, 16, "network ID");
        accountId = ExactNonzero(
            expectedIdentity.Account.AccountIdentity.AccountId.Bytes.Span,
            DeepAccountId32.Size,
            "account ID");
        deviceId = ExactNonzero(
            expectedIdentity.Device.DeviceId.Bytes.Span,
            DeviceId32.Size,
            "device ID");
        publicKey = ExactNonzero(
            expectedIdentity.Device.SigningPublicKey.Span,
            DeepIdentityCrypto.Ed25519PublicKeySize,
            "device signing public key");
        ArgumentException.ThrowIfNullOrWhiteSpace(
            expectedIdentity.SecureSlots.DeviceSigningKey);
        custodyDomainHash = ComputeCustodyDomainHash(expectedIdentity);
    }

    public ReadOnlyMemory<byte> DeviceId => deviceId.ToArray();

    public ReadOnlyMemory<byte> Ed25519PublicKey => publicKey.ToArray();

    public ReadOnlyMemory<byte> CustodyDomainHash => custodyDomainHash.ToArray();

    public async ValueTask<int> SignAsync(
        GroupDeviceSigningRequest request,
        Memory<byte> signature64,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (signature64.Length != 64)
        {
            throw new ArgumentException(
                "The Group v1 signature destination must contain exactly 64 bytes.",
                nameof(signature64));
        }

        signature64.Span.Clear();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequestBinding(request);

        var current = await accounts.GetLocalIdentityAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CryptographicException("No current Deep account owns this signer.");
        ValidateCurrentIdentity(current);

        using var owned = await secureStorage
            .ReadOwnedAsync(current.SecureSlots.DeviceSigningKey, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CryptographicException(
                "The current-device signing seed is missing from secure storage.");
        if (owned.Length != DeepIdentityCrypto.Ed25519SeedSize)
        {
            throw new CryptographicException(
                "The current-device signing seed has an invalid length.");
        }

        byte[]? seed = null;
        byte[]? signingInput = null;
        byte[]? signature = null;
        try
        {
            seed = new byte[DeepIdentityCrypto.Ed25519SeedSize];
            owned.CopyTo(seed);
            if (!DeepIdentityCrypto.Ed25519PublicKeyMatchesSeed(seed, publicKey))
            {
                throw new CryptographicException(
                    "The current-device signing seed does not match its reconciled public key.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            signingInput = request.SigningInput.ToArray();
            if (signingInput.Length == 0)
            {
                throw new CryptographicException("The Group v1 signing input is empty.");
            }

            using var keyPair = PublicKeyAuth.GenerateKeyPair(seed);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(keyPair.PublicKey, publicKey))
                {
                    throw new CryptographicException(
                        "The secure signing seed derived an unexpected public key.");
                }

                signature = PublicKeyAuth.SignDetached(signingInput, keyPair.PrivateKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyPair.PrivateKey);
                CryptographicOperations.ZeroMemory(keyPair.PublicKey);
            }
            if (signature.Length != 64
                || signature.AsSpan().IndexOfAnyExcept((byte)0) < 0
                || !PublicKeyAuth.VerifyDetached(signature, signingInput, publicKey))
            {
                throw new CryptographicException(
                    "The current-device Group v1 signature failed verification.");
            }

            current = await accounts.GetLocalIdentityAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new CryptographicException(
                    "The Deep account was removed while Group v1 was signing.");
            ValidateCurrentIdentity(current);
            cancellationToken.ThrowIfCancellationRequested();
            signature.CopyTo(signature64);
            return signature.Length;
        }
        catch
        {
            signature64.Span.Clear();
            throw;
        }
        finally
        {
            Zero(seed);
            Zero(signingInput);
            Zero(signature);
        }
    }

    private void ValidateRequestBinding(GroupDeviceSigningRequest request)
    {
        if (!Enum.IsDefined(request.Purpose))
        {
            throw new CryptographicException("The Group v1 signature purpose is unknown.");
        }
        RequireSame(request.NetworkId.Span, networkId, "network");
        RequireSame(request.AccountId.Span, accountId, "account");
        RequireSame(request.DeviceId.Span, deviceId, "device");
        RequireSame(request.CustodyDomainHash.Span, custodyDomainHash, "custody domain");
        RequireNonzero(request.GroupId.Span, 32, "group ID");
    }

    private void ValidateCurrentIdentity(DeepLocalIdentitySnapshot current)
    {
        if (current.StoreGeneration != expectedIdentity.StoreGeneration
            || current.Account.AccountIdentity.AccountGeneration
                != expectedIdentity.Account.AccountIdentity.AccountGeneration
            || current.Device.DeviceGeneration != expectedIdentity.Device.DeviceGeneration
            || !string.Equals(
                current.SecureSlots.DeviceSigningKey,
                expectedIdentity.SecureSlots.DeviceSigningKey,
                StringComparison.Ordinal))
        {
            throw new CryptographicException(
                "The Group v1 signer no longer belongs to the current account generation.");
        }

        RequireSame(current.NetworkId.Span, networkId, "current network");
        RequireSame(
            current.Account.AccountIdentity.AccountId.Bytes.Span,
            accountId,
            "current account");
        RequireSame(current.Device.DeviceId.Bytes.Span, deviceId, "current device");
        RequireSame(
            current.Device.SigningPublicKey.Span,
            publicKey,
            "current device signing key");
    }

    private static byte[] ComputeCustodyDomainHash(DeepLocalIdentitySnapshot identity)
    {
        var payload = new byte[
            CustodyDomain.Length + 16 + DeepAccountId32.Size + sizeof(ulong)
            + DeviceId32.Size + sizeof(ulong)];
        var offset = 0;
        CustodyDomain.CopyTo(payload);
        offset += CustodyDomain.Length;
        identity.NetworkId.Span.CopyTo(payload.AsSpan(offset, 16));
        offset += 16;
        identity.Account.AccountIdentity.AccountId.CopyTo(
            payload.AsSpan(offset, DeepAccountId32.Size));
        offset += DeepAccountId32.Size;
        BinaryPrimitives.WriteUInt64BigEndian(
            payload.AsSpan(offset, sizeof(ulong)),
            identity.Account.AccountIdentity.AccountGeneration);
        offset += sizeof(ulong);
        identity.Device.DeviceId.CopyTo(payload.AsSpan(offset, DeviceId32.Size));
        offset += DeviceId32.Size;
        BinaryPrimitives.WriteUInt64BigEndian(
            payload.AsSpan(offset, sizeof(ulong)),
            identity.Device.DeviceGeneration);
        try
        {
            var hash = SHA256.HashData(payload);
            if (hash.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                CryptographicOperations.ZeroMemory(hash);
                throw new CryptographicException("The Group v1 custody domain hash is zero.");
            }
            return hash;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static byte[] ExactNonzero(ReadOnlySpan<byte> value, int length, string name)
    {
        if (value.Length != length || value.IndexOfAnyExcept((byte)0) < 0)
        {
            throw new CryptographicException(
                $"The Group v1 {name} must contain exactly {length} nonzero bytes.");
        }
        return value.ToArray();
    }

    private static void RequireNonzero(ReadOnlySpan<byte> value, int length, string name)
    {
        if (value.Length != length || value.IndexOfAnyExcept((byte)0) < 0)
        {
            throw new CryptographicException(
                $"The Group v1 {name} must contain exactly {length} nonzero bytes.");
        }
    }

    private static void RequireSame(
        ReadOnlySpan<byte> actual,
        ReadOnlySpan<byte> expected,
        string name)
    {
        if (actual.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new CryptographicException(
                $"The Group v1 signing request belongs to another {name}.");
        }
    }

    private static void Zero(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }
}
