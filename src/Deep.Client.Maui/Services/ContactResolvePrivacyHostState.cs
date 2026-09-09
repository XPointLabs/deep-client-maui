using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Protocol.DeepExtension.PrivacyRouting;
using Deep.Protocol.Identity;
using Sodium;

namespace Deep.Client.Maui.Services;

internal sealed class ContactResolvePrivacyHostScope
{
    private readonly byte[] networkId;
    private readonly byte[] accountId;
    private readonly byte[] deviceId;
    private readonly byte[] storeInstanceId;
    private readonly byte[] agreementPublicKey;

    internal ContactResolvePrivacyHostScope(
        DeepLocalIdentitySnapshot identity,
        ReadOnlySpan<byte> messageStoreInstanceId)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Require(identity.NetworkId.Span, 16, nameof(identity));
        Require(identity.Account.AccountIdentity.AccountId.Bytes.Span, 32, nameof(identity));
        Require(identity.Device.DeviceId.Bytes.Span, 32, nameof(identity));
        Require(messageStoreInstanceId, 32, nameof(messageStoreInstanceId));
        Require(identity.Device.AgreementPublicKey.Span, 32, nameof(identity));
        if (identity.Account.AccountIdentity.AccountGeneration == 0
            || identity.Device.DeviceGeneration == 0)
        {
            throw new ArgumentException("ContactResolve host generations must be positive.",
                nameof(identity));
        }
        if (!identity.Account.AccountIdentity.NetworkId.Matches(identity.NetworkId.Span)
            || !identity.Account.CurrentDeviceId.Equals(identity.Device.DeviceId))
        {
            throw new CryptographicException(
                "ContactResolve host identity is not bound to the current account and device.");
        }

        networkId = identity.NetworkId.ToArray();
        accountId = identity.Account.AccountIdentity.AccountId.Bytes.ToArray();
        AccountGeneration = identity.Account.AccountIdentity.AccountGeneration;
        deviceId = identity.Device.DeviceId.Bytes.ToArray();
        DeviceGeneration = identity.Device.DeviceGeneration;
        storeInstanceId = messageStoreInstanceId.ToArray();
        agreementPublicKey = identity.Device.AgreementPublicKey.ToArray();
        DeviceIdSlot = identity.SecureSlots.DeviceId;
        DeviceAgreementKeySlot = identity.SecureSlots.DeviceAgreementKey;
        BindingHash = ComputeBindingHash();
        KeyHandleId = ComputeKeyHandleId();
    }

    internal ReadOnlyMemory<byte> NetworkId => networkId.ToArray();
    internal ReadOnlyMemory<byte> AccountId => accountId.ToArray();
    internal ulong AccountGeneration { get; }
    internal ReadOnlyMemory<byte> DeviceId => deviceId.ToArray();
    internal ulong DeviceGeneration { get; }
    internal ReadOnlyMemory<byte> StoreInstanceId => storeInstanceId.ToArray();
    internal ReadOnlyMemory<byte> AgreementPublicKey => agreementPublicKey.ToArray();
    internal string DeviceIdSlot { get; }
    internal string DeviceAgreementKeySlot { get; }
    internal ReadOnlyMemory<byte> BindingHash { get; }
    internal ReadOnlyMemory<byte> KeyHandleId { get; }

    internal bool Matches(
        ReadOnlySpan<byte> candidateNetwork,
        ReadOnlySpan<byte> candidateAccount,
        ulong candidateAccountGeneration,
        ReadOnlySpan<byte> candidateDevice,
        ulong candidateDeviceGeneration,
        ReadOnlySpan<byte> candidateStoreInstance) =>
        candidateAccountGeneration == AccountGeneration
        && candidateDeviceGeneration == DeviceGeneration
        && Fixed(networkId, candidateNetwork)
        && Fixed(accountId, candidateAccount)
        && Fixed(deviceId, candidateDevice)
        && Fixed(storeInstanceId, candidateStoreInstance);

    private byte[] ComputeBindingHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("Deep/ContactResolve/privacy-host-binding/v1\0"u8);
        hash.AppendData(networkId);
        hash.AppendData(accountId);
        AppendU64(hash, AccountGeneration);
        hash.AppendData(deviceId);
        AppendU64(hash, DeviceGeneration);
        hash.AppendData(storeInstanceId);
        hash.AppendData(agreementPublicKey);
        return hash.GetHashAndReset();
    }

    private byte[] ComputeKeyHandleId()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("Deep/ContactResolve/onion-key-handle/v1\0"u8);
        hash.AppendData(BindingHash.Span);
        return hash.GetHashAndReset();
    }

    private static void AppendU64(IncrementalHash hash, ulong value)
    {
        Span<byte> encoded = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(encoded, value);
        hash.AppendData(encoded);
        CryptographicOperations.ZeroMemory(encoded);
    }

    private static void Require(ReadOnlySpan<byte> value, int length, string name)
    {
        if (value.Length != length || value.IndexOfAnyExcept((byte)0) < 0)
        {
            throw new ArgumentException($"{name} contains an invalid binding value.", name);
        }
    }

    private static bool Fixed(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(left, right);
}

/// <summary>
/// Uses only the current device's protected agreement scalar. The opaque handle
/// is derived from the complete account/device/store binding and no private key
/// is retained outside one owned secure-storage read.
/// </summary>
internal sealed class AccountBoundOnionKeyAgreementVault(
    IDeepSecureStorage secureStorage,
    ContactResolvePrivacyHostScope scope) : IOnionKeyAgreementVault
{
    private readonly IDeepSecureStorage secureStorage = secureStorage
        ?? throw new ArgumentNullException(nameof(secureStorage));
    private readonly ContactResolvePrivacyHostScope scope = scope
        ?? throw new ArgumentNullException(nameof(scope));

    public async ValueTask<byte[]> DeriveX25519SharedSecretAsync(
        OnionKeyHandle keyHandle,
        ReadOnlyMemory<byte> peerPublicKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyHandle);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Fixed(keyHandle.Id.Span, scope.KeyHandleId.Span)
            || peerPublicKey.Length != 32
            || peerPublicKey.Span.IndexOfAnyExcept((byte)0) < 0)
        {
            throw new CryptographicException(
                "The onion key request is not bound to this account and device.");
        }

        using var persistedDevice = await secureStorage
            .ReadOwnedAsync(scope.DeviceIdSlot, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CryptographicException("The protected device binding is missing.");
        if (persistedDevice.Length != 32
            || !persistedDevice.Use(value => Fixed(value, scope.DeviceId.Span)))
        {
            throw new CryptographicException("The protected device binding changed.");
        }

        using var ownedScalar = await secureStorage
            .ReadOwnedAsync(scope.DeviceAgreementKeySlot, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CryptographicException("The protected agreement key is missing.");
        var scalar = new byte[32];
        var peer = peerPublicKey.ToArray();
        byte[]? shared = null;
        try
        {
            if (ownedScalar.Length != scalar.Length)
            {
                throw new CryptographicException("The protected agreement key is malformed.");
            }
            ownedScalar.CopyTo(scalar);
            if (!DeepIdentityCrypto.X25519PublicKeyMatchesPrivateScalar(
                    scalar,
                    scope.AgreementPublicKey.Span))
            {
                throw new CryptographicException(
                    "The protected agreement key does not own the bound public key.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            shared = ScalarMult.Mult(scalar, peer);
            if (shared.Length != 32 || shared.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                throw new CryptographicException("X25519 produced an invalid shared secret.");
            }
            var result = shared;
            shared = null;
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
            CryptographicOperations.ZeroMemory(peer);
            if (shared is not null)
            {
                CryptographicOperations.ZeroMemory(shared);
            }
        }
    }

    private static bool Fixed(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(left, right);
}

/// <summary>
/// Protected, bounded, all-or-none entropy commitment ledger. The encrypted
/// state is cross-checked against alternating secure-storage anchors so a stale
/// database, interrupted anchor update, corruption, or binding change never
/// becomes an empty ledger.
/// </summary>
internal sealed class ProtectedContactResolveEntropyLedger :
    IOnionEntropyUniquenessLedger,
    IDisposable
{
    internal const string StateFileName = "contact-resolve-entropy.cre1";
    private const int MaximumCommitments = 262_144;
    private const int FixedStateBytes = 212;
    private const int AnchorBytes = 80;
    private const ushort FormatVersion = 1;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessGates =
        new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    private readonly string statePath;
    private readonly string lockPath;
    private readonly string[] anchorSlots;
    private readonly IDeepSecretProtector protector;
    private readonly IDeepSecureStorage secureStorage;
    private readonly ContactResolvePrivacyHostScope scope;
    private readonly SemaphoreSlim gate;
    private int disposed;

    private ProtectedContactResolveEntropyLedger(
        string statePath,
        string anchorSlot0,
        string anchorSlot1,
        IDeepSecretProtector protector,
        IDeepSecureStorage secureStorage,
        ContactResolvePrivacyHostScope scope)
    {
        this.statePath = Path.GetFullPath(statePath);
        lockPath = this.statePath + ".lock";
        anchorSlots = [anchorSlot0, anchorSlot1];
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        this.secureStorage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
        this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
        gate = ProcessGates.GetOrAdd(this.statePath, static _ => new SemaphoreSlim(1, 1));
    }

    internal static async Task<ProtectedContactResolveEntropyLedger> OpenAsync(
        string statePath,
        string anchorSlot0,
        string anchorSlot1,
        IDeepSecretProtector protector,
        IDeepSecureStorage secureStorage,
        ContactResolvePrivacyHostScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorSlot0);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorSlot1);
        if (string.Equals(anchorSlot0, anchorSlot1, StringComparison.Ordinal))
        {
            throw new ArgumentException("Entropy anchor slots must be distinct.");
        }
        var parent = Path.GetDirectoryName(Path.GetFullPath(statePath));
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new ArgumentException("Entropy state has no parent directory.", nameof(statePath));
        }
        Directory.CreateDirectory(parent);
        var ledger = new ProtectedContactResolveEntropyLedger(
            statePath,
            anchorSlot0,
            anchorSlot1,
            protector,
            secureStorage,
            scope);
        try
        {
            await ledger.ValidateOrInitializeAsync(cancellationToken).ConfigureAwait(false);
            return ledger;
        }
        catch
        {
            ledger.Dispose();
            throw;
        }
    }

    public async ValueTask<OnionEntropyCommitOutcome> CommitAsync(
        OnionEntropyCommitmentBatch batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        var additions = batch.Commitments.Select(static value => value.ToArray()).ToArray();
        try
        {
            if (additions.Length == 0
                || additions.Any(static value => value.Length != 32
                    || value.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                || additions.Distinct(ByteArrayComparer.Instance).Count() != additions.Length)
            {
                return OnionEntropyCommitOutcome.Rejected;
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                using var processLock = OpenProcessLock();
                var current = await ReadVerifiedStateAsync(cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    if (additions.Any(addition => current.Commitments.Contains(
                            addition,
                            ByteArrayComparer.Instance)))
                    {
                        return OnionEntropyCommitOutcome.Duplicate;
                    }
                    if (current.Commitments.Count > MaximumCommitments - additions.Length)
                    {
                        return OnionEntropyCommitOutcome.Rejected;
                    }

                    var nextCommitments = current.Commitments
                        .Select(static value => value.ToArray())
                        .Concat(additions.Select(static value => value.ToArray()))
                        .OrderBy(static value => value, ByteArrayComparer.Instance)
                        .ToArray();
                    var next = new LedgerState(
                        checked(current.Revision + 1),
                        current.StateHash.ToArray(),
                        nextCommitments);
                    try
                    {
                        WriteState(next, cancellationToken);
                        try
                        {
                            await WriteAnchorAsync(next, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            return OnionEntropyCommitOutcome.Ambiguous;
                        }
                        return OnionEntropyCommitOutcome.Committed;
                    }
                    finally
                    {
                        Clear(next);
                    }
                }
                finally
                {
                    Clear(current);
                }
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            Zero(additions);
        }
    }

    public void Dispose() => Interlocked.Exchange(ref disposed, 1);

    private async Task ValidateOrInitializeAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = OpenProcessLock();
            var anchors = await ReadAnchorsAsync(cancellationToken).ConfigureAwait(false);
            if (!File.Exists(statePath))
            {
                if (anchors.Count != 0)
                {
                    throw Corrupt("Entropy state is missing while a protected anchor exists.");
                }
                var initial = new LedgerState(0, new byte[32], []);
                try
                {
                    WriteState(initial, cancellationToken);
                    await WriteAnchorAsync(initial, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(initial.PreviousHash);
                    CryptographicOperations.ZeroMemory(initial.StateHash);
                }
                return;
            }
            var verified = await VerifyStateAgainstAnchorsAsync(
                    ReadState(),
                    anchors,
                    cancellationToken)
                .ConfigureAwait(false);
            Clear(verified);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<LedgerState> ReadVerifiedStateAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(statePath))
        {
            throw Corrupt("Entropy state disappeared after initialization.");
        }
        var anchors = await ReadAnchorsAsync(cancellationToken).ConfigureAwait(false);
        return await VerifyStateAgainstAnchorsAsync(
                ReadState(),
                anchors,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<LedgerState> VerifyStateAgainstAnchorsAsync(
        LedgerState state,
        IReadOnlyList<LedgerAnchor> anchors,
        CancellationToken cancellationToken)
    {
        var accepted = false;
        try
        {
            if (anchors.Count == 0)
            {
                throw Corrupt("Entropy state has no protected rollback anchor.");
            }
            var highest = anchors.OrderByDescending(static value => value.Revision).First();
            if (state.Revision == highest.Revision
                && Fixed(state.StateHash, highest.StateHash))
            {
                accepted = true;
                return state;
            }
            if (state.Revision == checked(highest.Revision + 1)
                && Fixed(state.PreviousHash, highest.StateHash))
            {
                await WriteAnchorAsync(state, cancellationToken).ConfigureAwait(false);
                accepted = true;
                return state;
            }
            throw Corrupt("Entropy state was rolled back or does not match its protected anchor.");
        }
        finally
        {
            foreach (var anchor in anchors)
            {
                CryptographicOperations.ZeroMemory(anchor.StateHash);
            }
            if (!accepted)
            {
                Clear(state);
            }
        }
    }

    private LedgerState ReadState()
    {
        var protectedBytes = File.ReadAllBytes(statePath);
        byte[]? clear = null;
        try
        {
            if (protectedBytes.Length == 0 || protectedBytes.Length > 64 * 1024 * 1024)
            {
                throw Corrupt("Entropy state has an invalid protected length.");
            }
            clear = protector.Unprotect(protectedBytes);
            return DecodeState(clear);
        }
        catch (CryptographicException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not IOException)
        {
            throw Corrupt("Entropy state authentication or decoding failed.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (clear is not null)
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
    }

    private LedgerState DecodeState(ReadOnlySpan<byte> value)
    {
        if (value.Length < FixedStateBytes
            || !value[..4].SequenceEqual("CRE1"u8)
            || BinaryPrimitives.ReadUInt16BigEndian(value[4..]) != FormatVersion
            || BinaryPrimitives.ReadUInt16BigEndian(value[6..]) != 0)
        {
            throw Corrupt("Entropy state header is invalid.");
        }
        var revision = BinaryPrimitives.ReadUInt64BigEndian(value[8..]);
        var offset = 16;
        var network = value.Slice(offset, 16); offset += 16;
        var account = value.Slice(offset, 32); offset += 32;
        var accountGeneration = BinaryPrimitives.ReadUInt64BigEndian(value[offset..]); offset += 8;
        var device = value.Slice(offset, 32); offset += 32;
        var deviceGeneration = BinaryPrimitives.ReadUInt64BigEndian(value[offset..]); offset += 8;
        var instance = value.Slice(offset, 32); offset += 32;
        if (!scope.Matches(
                network,
                account,
                accountGeneration,
                device,
                deviceGeneration,
                instance))
        {
            throw Corrupt("Entropy state belongs to another account, device, or network.");
        }
        var previousHash = value.Slice(offset, 32).ToArray(); offset += 32;
        var count = BinaryPrimitives.ReadUInt32BigEndian(value[offset..]); offset += 4;
        if (count > MaximumCommitments
            || value.Length != FixedStateBytes + checked((int)count * 32))
        {
            CryptographicOperations.ZeroMemory(previousHash);
            throw Corrupt("Entropy state commitment count is invalid.");
        }
        var commitments = new byte[checked((int)count)][];
        try
        {
            for (var index = 0; index < commitments.Length; index++)
            {
                commitments[index] = value.Slice(offset, 32).ToArray();
                offset += 32;
                if (commitments[index].AsSpan().IndexOfAnyExcept((byte)0) < 0
                    || index > 0
                    && ByteArrayComparer.Instance.Compare(
                        commitments[index - 1],
                        commitments[index]) >= 0)
                {
                    throw Corrupt("Entropy commitments are malformed or not unique.");
                }
            }
            var expectedHash = value.Slice(offset, 32);
            var actualHash = SHA256.HashData(value[..offset]);
            try
            {
                if (!Fixed(actualHash, expectedHash))
                {
                    throw Corrupt("Entropy state hash is invalid.");
                }
                return new LedgerState(revision, previousHash, commitments, actualHash.ToArray());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actualHash);
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(previousHash);
            Zero(commitments);
            throw;
        }
    }

    private void WriteState(LedgerState state, CancellationToken cancellationToken)
    {
        var clear = EncodeState(state);
        byte[]? protectedBytes = null;
        var temporary = statePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            protectedBytes = protector.Protect(clear);
            cancellationToken.ThrowIfCancellationRequested();
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
            CryptographicOperations.ZeroMemory(clear);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    private byte[] EncodeState(LedgerState state)
    {
        var output = new byte[FixedStateBytes + checked(state.Commitments.Count * 32)];
        "CRE1"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(8), state.Revision);
        var offset = 16;
        scope.NetworkId.Span.CopyTo(output.AsSpan(offset)); offset += 16;
        scope.AccountId.Span.CopyTo(output.AsSpan(offset)); offset += 32;
        BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(offset), scope.AccountGeneration); offset += 8;
        scope.DeviceId.Span.CopyTo(output.AsSpan(offset)); offset += 32;
        BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(offset), scope.DeviceGeneration); offset += 8;
        scope.StoreInstanceId.Span.CopyTo(output.AsSpan(offset)); offset += 32;
        state.PreviousHash.CopyTo(output.AsSpan(offset)); offset += 32;
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(offset), checked((uint)state.Commitments.Count)); offset += 4;
        foreach (var commitment in state.Commitments)
        {
            commitment.CopyTo(output, offset);
            offset += 32;
        }
        var hash = SHA256.HashData(output.AsSpan(0, offset));
        hash.CopyTo(output, offset);
        CryptographicOperations.ZeroMemory(state.StateHash);
        state.StateHash = hash.ToArray();
        CryptographicOperations.ZeroMemory(hash);
        return output;
    }

    private async Task<IReadOnlyList<LedgerAnchor>> ReadAnchorsAsync(
        CancellationToken cancellationToken)
    {
        var anchors = new List<LedgerAnchor>(2);
        foreach (var slot in anchorSlots)
        {
            using var owned = await secureStorage.ReadOwnedAsync(slot, cancellationToken)
                .ConfigureAwait(false);
            if (owned is null)
            {
                continue;
            }
            var value = new byte[owned.Length];
            try
            {
                owned.CopyTo(value);
                anchors.Add(DecodeAnchor(value));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(value);
            }
        }
        if (anchors.Count == 2
            && anchors[0].Revision == anchors[1].Revision
            && !Fixed(anchors[0].StateHash, anchors[1].StateHash))
        {
            foreach (var anchor in anchors)
            {
                CryptographicOperations.ZeroMemory(anchor.StateHash);
            }
            throw Corrupt("Protected entropy anchors conflict at one revision.");
        }
        return anchors;
    }

    private LedgerAnchor DecodeAnchor(ReadOnlySpan<byte> value)
    {
        if (value.Length != AnchorBytes
            || !value[..4].SequenceEqual("CRA1"u8)
            || BinaryPrimitives.ReadUInt16BigEndian(value[4..]) != FormatVersion
            || BinaryPrimitives.ReadUInt16BigEndian(value[6..]) != 0
            || !Fixed(value.Slice(16, 32), scope.BindingHash.Span))
        {
            throw Corrupt("Protected entropy anchor is malformed or cross-bound.");
        }
        return new LedgerAnchor(
            BinaryPrimitives.ReadUInt64BigEndian(value[8..]),
            value.Slice(48, 32).ToArray());
    }

    private async Task WriteAnchorAsync(
        LedgerState state,
        CancellationToken cancellationToken)
    {
        var targetIndex = checked((int)(state.Revision & 1));
        var target = anchorSlots[targetIndex];
        var prior = anchorSlots[1 - targetIndex];
        var encoded = new byte[AnchorBytes];
        try
        {
            "CRA1"u8.CopyTo(encoded);
            BinaryPrimitives.WriteUInt16BigEndian(encoded.AsSpan(4), FormatVersion);
            BinaryPrimitives.WriteUInt64BigEndian(encoded.AsSpan(8), state.Revision);
            scope.BindingHash.Span.CopyTo(encoded.AsSpan(16));
            state.StateHash.CopyTo(encoded, 48);
            await secureStorage.DeleteBatchAsync([target], cancellationToken)
                .ConfigureAwait(false);
            await secureStorage.WriteBatchAsync(
                    [new DeepSecureStorageWrite(target, encoded)],
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await secureStorage.DeleteBatchAsync([prior], CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or CryptographicException)
            {
                // The newer anchor is already durable. A stale lower anchor is safe.
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private FileStream OpenProcessLock() => new(
        lockPath,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None,
        1,
        FileOptions.WriteThrough);

    private static CryptographicException Corrupt(
        string message,
        Exception? inner = null) =>
        new(message, inner);

    private static bool Fixed(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(left, right);

    private static void Zero(IEnumerable<byte[]> values)
    {
        foreach (var value in values)
        {
            if (value is not null)
            {
                CryptographicOperations.ZeroMemory(value);
            }
        }
    }

    private static void Clear(LedgerState state)
    {
        Zero(state.Commitments);
        CryptographicOperations.ZeroMemory(state.PreviousHash);
        CryptographicOperations.ZeroMemory(state.StateHash);
    }

    private sealed class LedgerState
    {
        internal LedgerState(
            ulong revision,
            byte[] previousHash,
            IReadOnlyList<byte[]> commitments,
            byte[]? stateHash = null)
        {
            Revision = revision;
            PreviousHash = previousHash;
            Commitments = commitments;
            StateHash = stateHash ?? new byte[32];
        }

        internal ulong Revision { get; }
        internal byte[] PreviousHash { get; }
        internal IReadOnlyList<byte[]> Commitments { get; }
        internal byte[] StateHash { get; set; }
    }

    private sealed record LedgerAnchor(ulong Revision, byte[] StateHash);

    private sealed class ByteArrayComparer : IComparer<byte[]>, IEqualityComparer<byte[]>
    {
        internal static ByteArrayComparer Instance { get; } = new();

        public int Compare(byte[]? left, byte[]? right) =>
            left.AsSpan().SequenceCompareTo(right);

        public bool Equals(byte[]? left, byte[]? right) =>
            left is not null && right is not null && Fixed(left, right);

        public int GetHashCode(byte[] value) =>
            BinaryPrimitives.ReadInt32BigEndian(value);
    }
}
