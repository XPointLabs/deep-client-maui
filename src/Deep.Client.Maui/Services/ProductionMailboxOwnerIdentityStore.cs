using System.Security.Cryptography;
using System.Text;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.MailboxAuthority;
using Microsoft.Maui.Storage;
using Sodium;

namespace Deep.Client.Maui.Services;

/// <summary>
/// Durable active-account-generation mailbox owner identity. Its key is distinct from the
/// Session holder signing key, but it follows the same account replacement/purge lifecycle and
/// exposes only the exact LocalOwner PHP1 signature.
/// </summary>
internal sealed class ProductionMailboxOwnerIdentity : IDisposable
{
    private readonly byte[] publicKey;
    private readonly byte[] privateKey;
    private int disposed;

    internal ProductionMailboxOwnerIdentity(byte[] seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (seed.Length != 32 || seed.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            throw new InvalidDataException("Production mailbox owner seed is invalid.");
        var pair = PublicKeyAuth.GenerateKeyPair(seed);
        try
        {
            publicKey = pair.PublicKey.ToArray();
            privateKey = pair.PrivateKey.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pair.PrivateKey);
            CryptographicOperations.ZeroMemory(pair.PublicKey);
        }
    }

    public byte[] GetPublicKey()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return publicKey.ToArray();
    }

    public byte[] SignLocalOwnerProof(ProductionMailboxHolderProofInput input)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Intent != ProductionMailboxIssuanceIntent.LocalOwner ||
            input.MailboxOwnerEd25519PublicKey.Length != publicKey.Length ||
            !CryptographicOperations.FixedTimeEquals(
                input.MailboxOwnerEd25519PublicKey.Span, publicKey))
        {
            throw new InvalidOperationException(
                "Production mailbox owner identity signs only its exact LocalOwner PHP1 transcript.");
        }
        return PublicKeyAuth.SignDetached(
            ProductionMailboxHolderProof.GetSigningBytes(input), privateKey);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        CryptographicOperations.ZeroMemory(privateKey);
        CryptographicOperations.ZeroMemory(publicKey);
    }
}

internal static class ProductionMailboxOwnerIdentityStore
{
    internal const string ActiveSlotKey =
        "deep.account.identity-bundle.active.v2";
    internal const string SlotAKey =
        "deep.account.identity-bundle.a.v2";
    internal const string SlotBKey =
        "deep.account.identity-bundle.b.v2";
    internal const string DeletionTombstoneKey =
        "deep.account.identity-bundle.deleting.v2";
    internal const string ReservationKey =
        "deep.account.identity-bundle.reserved.v2";
    private const string DeletionTombstoneValue = "delete-all-v2";
    private const string CrossProcessLockFileName =
        "deep.account.identity-bundle.lock.v2";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal enum CommitFaultPoint
    {
        AfterStagingSlot,
        AfterStaging,
        AfterActivation,
        AfterCleanup,
        AfterDeletionTombstone,
        AfterDeletionDurableMutation,
        AfterDeletionPointerRemoval,
        AfterDeletionFirstSlotRemoval,
        AfterDeletionSecondSlotRemoval
    }

    public static async Task<ProductionMailboxOwnerIdentity> LoadForAccountAsync(
        SessionId account,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? seed = null;
        try
        {
            using var processLock = await AcquireCrossProcessLockAsync(cancellationToken)
                .ConfigureAwait(false);
            await RecoverInterruptedDeletionAsync().ConfigureAwait(false);
            var bundle = await FindBundleAsync(account).ConfigureAwait(false) ??
                throw new InvalidOperationException(
                    "The durable account recovery identity is unavailable.");
            seed = bundle.Seed;
            await ReconcileBundleForDurableAccountAsync(
                account, bundle.Slot, null).ConfigureAwait(false);
            return new ProductionMailboxOwnerIdentity(seed);
        }
        finally
        {
            if (seed is not null) CryptographicOperations.ZeroMemory(seed);
            Gate.Release();
        }
    }

    internal static async Task<string?> GetRecoveryPhraseForDurableAccountAsync(
        Func<Task<SessionId?>> readDurableAccount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readDurableAccount);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? seed = null;
        try
        {
            using var processLock = await AcquireCrossProcessLockAsync(cancellationToken)
                .ConfigureAwait(false);
            await RecoverInterruptedDeletionAsync().ConfigureAwait(false);
            var account = await readDurableAccount().ConfigureAwait(false);
            if (account is null) return null;
            var bundle = await FindBundleAsync(account.Value).ConfigureAwait(false);
            if (bundle is null) return null;
            seed = bundle.Value.Seed;
            await ReconcileBundleForDurableAccountAsync(
                account.Value, bundle.Value.Slot, null).ConfigureAwait(false);
            return bundle.Value.Phrase;
        }
        finally
        {
            if (seed is not null) CryptographicOperations.ZeroMemory(seed);
            Gate.Release();
        }
    }

    internal static async Task StageAccountAsync(
        string phrase,
        Action<CommitFaultPoint>? faultInjector = null,
        CancellationToken cancellationToken = default)
    {
        if (!SessionAccountService.IsCanonicalRecoveryPhrase(phrase))
            throw new InvalidDataException("Recovery phrase is not canonical.");
        using var identity = new SessionIdentityProvider(phrase);
        var account = identity.SessionId;
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? seed = null;
        try
        {
            using var processLock = await AcquireCrossProcessLockAsync(cancellationToken)
                .ConfigureAwait(false);
            await RecoverInterruptedDeletionAsync().ConfigureAwait(false);
            var active = await ReadActiveSlotAsync().ConfigureAwait(false);
            var reservation = await ReadReservationAsync().ConfigureAwait(false);
            var existing = await FindBundleAsync(account).ConfigureAwait(false);
            if (existing is not null)
            {
                seed = existing.Value.Seed;
                if (!string.Equals(existing.Value.Phrase, phrase, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "Recovery phrase differs for the same account generation.");
                if (string.Equals(active, existing.Value.Slot, StringComparison.Ordinal) ||
                    reservation is not null &&
                    reservation.Value.Account == account &&
                    string.Equals(
                        reservation.Value.Slot, existing.Value.Slot, StringComparison.Ordinal))
                    return;
                if (reservation is null)
                {
                    await SetAndVerifyAsync(
                        ReservationKey,
                        EncodeReservation(existing.Value.Slot, account),
                        "Recovery identity staging reservation did not persist atomically.")
                        .ConfigureAwait(false);
                    faultInjector?.Invoke(CommitFaultPoint.AfterStaging);
                    return;
                }
                throw new InvalidDataException(
                    "Recovery identity staging reservation is inconsistent.");
            }
            if (reservation is not null)
                throw new InvalidOperationException(
                    "A different account generation is already staged.");
            var next = string.Equals(active, "a", StringComparison.Ordinal) ? "b" : "a";
            var nextKey = SlotKey(next);
            if (await SecureStorage.Default.GetAsync(nextKey).ConfigureAwait(false) is not null)
                throw new InvalidOperationException(
                    "A different account generation is already staged.");
            seed = RandomNumberGenerator.GetBytes(32);
            var payload = EncodeBundle(account, phrase, seed);
            await SetAndVerifyAsync(
                nextKey, payload, "Recovery identity staging did not persist atomically.")
                .ConfigureAwait(false);
            faultInjector?.Invoke(CommitFaultPoint.AfterStagingSlot);
            await SetAndVerifyAsync(
                ReservationKey,
                EncodeReservation(next, account),
                "Recovery identity staging reservation did not persist atomically.")
                .ConfigureAwait(false);
            faultInjector?.Invoke(CommitFaultPoint.AfterStaging);
        }
        finally
        {
            if (seed is not null) CryptographicOperations.ZeroMemory(seed);
            Gate.Release();
        }
    }

    internal static async Task CommitStagedAccountAsync(
        SessionId account,
        Func<Task> commitDurableAccount,
        Action<CommitFaultPoint>? faultInjector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commitDurableAccount);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? seed = null;
        try
        {
            using var processLock = await AcquireCrossProcessLockAsync(cancellationToken)
                .ConfigureAwait(false);
            await RecoverInterruptedDeletionAsync().ConfigureAwait(false);
            var bundle = await FindBundleAsync(account).ConfigureAwait(false) ??
                throw new InvalidOperationException(
                    "The staged recovery identity does not match the durable account.");
            seed = bundle.Seed;
            var active = await ReadActiveSlotAsync().ConfigureAwait(false);
            var reservation = await ReadReservationAsync().ConfigureAwait(false);
            var reserved = reservation is not null &&
                reservation.Value.Account == account &&
                string.Equals(reservation.Value.Slot, bundle.Slot, StringComparison.Ordinal);
            if (!reserved && !string.Equals(active, bundle.Slot, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The account generation has no durable staging reservation.");
            await commitDurableAccount().ConfigureAwait(false);
            if (reserved)
                await ActivateReservedSlotRecoveringAmbiguousOutcomeAsync(
                    bundle.Slot, faultInjector).ConfigureAwait(false);
        }
        finally
        {
            if (seed is not null) CryptographicOperations.ZeroMemory(seed);
            Gate.Release();
        }
    }

    internal static async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = await AcquireCrossProcessLockAsync(cancellationToken)
                .ConfigureAwait(false);
            await BeginDeletionAsync(null).ConfigureAwait(false);
            await CompleteDeletionAsync(null).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static async Task RemoveAfterDurableMutationAsync(
        Func<Task> durableMutation,
        Action<CommitFaultPoint>? faultInjector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(durableMutation);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = await AcquireCrossProcessLockAsync(cancellationToken)
                .ConfigureAwait(false);
            await RecoverInterruptedDeletionAsync().ConfigureAwait(false);
            await BeginDeletionAsync(faultInjector).ConfigureAwait(false);
            await durableMutation().ConfigureAwait(false);
            faultInjector?.Invoke(CommitFaultPoint.AfterDeletionDurableMutation);
            await CompleteDeletionAsync(faultInjector).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task BeginDeletionAsync(Action<CommitFaultPoint>? faultInjector)
    {
        await SetAndVerifyAsync(DeletionTombstoneKey, DeletionTombstoneValue,
            "Recovery identity deletion tombstone did not persist atomically.")
            .ConfigureAwait(false);
        faultInjector?.Invoke(CommitFaultPoint.AfterDeletionTombstone);
    }

    private static async Task CompleteDeletionAsync(Action<CommitFaultPoint>? faultInjector)
    {
        await RemoveAndVerifyAsync(ActiveSlotKey).ConfigureAwait(false);
        faultInjector?.Invoke(CommitFaultPoint.AfterDeletionPointerRemoval);
        await RemoveAndVerifyAsync(ReservationKey).ConfigureAwait(false);
        await RemoveAndVerifyAsync(SlotAKey).ConfigureAwait(false);
        faultInjector?.Invoke(CommitFaultPoint.AfterDeletionFirstSlotRemoval);
        await RemoveAndVerifyAsync(SlotBKey).ConfigureAwait(false);
        faultInjector?.Invoke(CommitFaultPoint.AfterDeletionSecondSlotRemoval);
        await RemoveAndVerifyAsync(DeletionTombstoneKey).ConfigureAwait(false);
    }

    private static async Task RecoverInterruptedDeletionAsync()
    {
        var tombstone = await SecureStorage.Default.GetAsync(DeletionTombstoneKey)
            .ConfigureAwait(false);
        if (tombstone is null) return;
        if (!string.Equals(tombstone, DeletionTombstoneValue, StringComparison.Ordinal))
            throw new InvalidDataException("Recovery identity deletion tombstone is corrupt.");
        await RemoveAndVerifyAsync(ActiveSlotKey).ConfigureAwait(false);
        await RemoveAndVerifyAsync(ReservationKey).ConfigureAwait(false);
        await RemoveAndVerifyAsync(SlotAKey).ConfigureAwait(false);
        await RemoveAndVerifyAsync(SlotBKey).ConfigureAwait(false);
        await RemoveAndVerifyAsync(DeletionTombstoneKey).ConfigureAwait(false);
    }

    private static async Task SetAndVerifyAsync(
        string key, string value, string failureMessage)
    {
        try
        {
            await SecureStorage.Default.SetAsync(key, value).ConfigureAwait(false);
        }
        catch
        {
            if (!string.Equals(
                    value,
                    await SecureStorage.Default.GetAsync(key).ConfigureAwait(false),
                    StringComparison.Ordinal))
                throw;
        }
        if (!string.Equals(
                value,
                await SecureStorage.Default.GetAsync(key).ConfigureAwait(false),
                StringComparison.Ordinal))
            throw new InvalidOperationException(failureMessage);
    }

    private static async Task RemoveAndVerifyAsync(string key)
    {
        SecureStorage.Default.Remove(key);
        if (await SecureStorage.Default.GetAsync(key).ConfigureAwait(false) is not null)
            throw new InvalidOperationException(
                "Recovery identity deletion did not persist atomically.");
    }

    internal static Task<FileStream> AcquireCrossProcessLockForTestAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        AcquireCrossProcessLockAsync(path, cancellationToken);

    private static Task<FileStream> AcquireCrossProcessLockAsync(
        CancellationToken cancellationToken) =>
        AcquireCrossProcessLockAsync(
            Path.Combine(FileSystem.AppDataDirectory, CrossProcessLockFileName),
            cancellationToken);

    private static async Task<FileStream> AcquireCrossProcessLockAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ??
            throw new InvalidDataException("Recovery identity lock path is invalid."));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    fullPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.WriteThrough);
            }
            catch (IOException exception) when (IsLockContention(exception))
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsLockContention(IOException exception)
    {
        var nativeCode = exception.HResult & 0xffff;
        return OperatingSystem.IsWindows()
            ? nativeCode is 32 or 33
            : nativeCode == 11;
    }

    private static async Task ReconcileBundleForDurableAccountAsync(
        SessionId account,
        string slot,
        Action<CommitFaultPoint>? faultInjector)
    {
        var active = await ReadActiveSlotAsync().ConfigureAwait(false);
        var reservation = await ReadReservationAsync().ConfigureAwait(false);
        if (reservation is not null &&
            reservation.Value.Account == account &&
            string.Equals(reservation.Value.Slot, slot, StringComparison.Ordinal))
        {
            await ActivateReservedSlotRecoveringAmbiguousOutcomeAsync(slot, faultInjector)
                .ConfigureAwait(false);
            return;
        }
        if (!string.Equals(active, slot, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The durable account identity is neither active nor reserved.");
    }

    private static async Task ActivateReservedSlotRecoveringAmbiguousOutcomeAsync(
        string slot,
        Action<CommitFaultPoint>? faultInjector)
    {
        try
        {
            await SecureStorage.Default.SetAsync(ActiveSlotKey, slot).ConfigureAwait(false);
        }
        catch
        {
            if (!string.Equals(
                    slot,
                    await SecureStorage.Default.GetAsync(ActiveSlotKey).ConfigureAwait(false),
                    StringComparison.Ordinal))
                throw;
        }
        if (!string.Equals(
                slot,
                await SecureStorage.Default.GetAsync(ActiveSlotKey).ConfigureAwait(false),
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Recovery identity activation did not persist atomically.");
        faultInjector?.Invoke(CommitFaultPoint.AfterActivation);
        await RemoveAndVerifyAsync(SlotKey(slot == "a" ? "b" : "a"))
            .ConfigureAwait(false);
        faultInjector?.Invoke(CommitFaultPoint.AfterCleanup);
        await RemoveAndVerifyAsync(ReservationKey).ConfigureAwait(false);
    }

    private static async Task<string?> ReadActiveSlotAsync()
    {
        var active = await SecureStorage.Default.GetAsync(ActiveSlotKey)
            .ConfigureAwait(false);
        ValidateSlot(active, allowMissing: true);
        return active;
    }

    private static string EncodeReservation(string slot, SessionId account)
    {
        byte[] accountBytes = [];
        try
        {
            accountBytes = StrictUtf8.GetBytes(account.Value);
            return "v2." + slot + "." + Base64Url(accountBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(accountBytes);
        }
    }

    private static async Task<(string Slot, SessionId Account)?> ReadReservationAsync()
    {
        var encoded = await SecureStorage.Default.GetAsync(ReservationKey)
            .ConfigureAwait(false);
        if (encoded is null) return null;
        if (encoded.Length is 0 or > 512)
            throw new InvalidDataException("Recovery identity reservation is corrupt.");
        var fields = encoded.Split('.', StringSplitOptions.None);
        if (fields.Length != 3 || fields[0] != "v2" || fields[1] is not ("a" or "b"))
            throw new InvalidDataException("Recovery identity reservation is corrupt.");
        byte[] accountBytes = [];
        try
        {
            accountBytes = DecodeBase64Url(fields[2]);
            var account = new SessionId(StrictUtf8.GetString(accountBytes));
            if (!string.Equals(
                    EncodeReservation(fields[1], account), encoded, StringComparison.Ordinal))
                throw new InvalidDataException("Recovery identity reservation is corrupt.");
            var payload = await SecureStorage.Default.GetAsync(SlotKey(fields[1]))
                .ConfigureAwait(false) ??
                throw new InvalidDataException(
                    "Recovery identity reservation has no staged bundle.");
            var bundle = DecodeBundle(payload);
            try
            {
                if (bundle.Account != account)
                    throw new InvalidDataException(
                        "Recovery identity reservation account binding is corrupt.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bundle.Seed);
            }
            return (fields[1], account);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new InvalidDataException("Recovery identity reservation is corrupt.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(accountBytes);
        }
    }

    private static async Task<(string Slot, string Phrase, byte[] Seed)?> FindBundleAsync(
        SessionId account)
    {
        (string Slot, string Phrase, byte[] Seed)? match = null;
        var succeeded = false;
        try
        {
            foreach (var slot in new[] { "a", "b" })
            {
                var payload = await SecureStorage.Default.GetAsync(SlotKey(slot))
                    .ConfigureAwait(false);
                if (payload is null) continue;
                var bundle = DecodeBundle(payload);
                if (bundle.Account != account)
                {
                    CryptographicOperations.ZeroMemory(bundle.Seed);
                    continue;
                }
                if (match is not null)
                {
                    CryptographicOperations.ZeroMemory(bundle.Seed);
                    throw new InvalidDataException(
                        "Recovery identity has duplicate account generations.");
                }
                match = (slot, bundle.Phrase, bundle.Seed);
            }
            succeeded = true;
            return match;
        }
        finally
        {
            if (!succeeded && match is not null)
                CryptographicOperations.ZeroMemory(match.Value.Seed);
        }
    }

    private static string SlotKey(string slot) => slot switch
    {
        "a" => SlotAKey,
        "b" => SlotBKey,
        _ => throw new InvalidDataException("Active recovery identity pointer is corrupt.")
    };

    private static void ValidateSlot(string? slot, bool allowMissing)
    {
        if (slot is null && allowMissing) return;
        if (slot is not ("a" or "b"))
            throw new InvalidDataException("Active recovery identity pointer is corrupt.");
    }

    private static string EncodeBundle(
        SessionId account, string phrase, ReadOnlySpan<byte> seed)
    {
        byte[] accountBytes = [];
        byte[] phraseBytes = [];
        try
        {
            accountBytes = Encoding.UTF8.GetBytes(account.Value);
            phraseBytes = Encoding.UTF8.GetBytes(phrase);
            return "v2." + Base64Url(accountBytes) + "." +
                Base64Url(phraseBytes) + "." + Base64Url(seed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(accountBytes);
            CryptographicOperations.ZeroMemory(phraseBytes);
        }
    }

    private static (SessionId Account, string Phrase, byte[] Seed) DecodeBundle(string encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > 2_048)
            throw new InvalidDataException("Active recovery identity is corrupt.");
        var fields = encoded.Split('.', StringSplitOptions.None);
        if (fields.Length != 4 || fields[0] != "v2")
            throw new InvalidDataException("Active recovery identity is corrupt.");
        byte[] accountBytes = [];
        byte[] phraseBytes = [];
        byte[] seed = [];
        var succeeded = false;
        try
        {
            accountBytes = DecodeBase64Url(fields[1]);
            phraseBytes = DecodeBase64Url(fields[2]);
            seed = DecodeBase64Url(fields[3]);
            var account = new SessionId(StrictUtf8.GetString(accountBytes));
            var phrase = StrictUtf8.GetString(phraseBytes);
            if (!SessionAccountService.IsCanonicalRecoveryPhrase(phrase) ||
                seed.Length != 32 || seed.AsSpan().IndexOfAnyExcept((byte)0) < 0 ||
                !string.Equals(
                    EncodeBundle(account, phrase, seed), encoded, StringComparison.Ordinal))
                throw new InvalidDataException("Active recovery identity is corrupt.");
            using var identity = new SessionIdentityProvider(phrase);
            if (identity.SessionId != account)
                throw new InvalidDataException(
                    "Recovery identity account binding is corrupt.");
            succeeded = true;
            return (account, phrase, seed);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new InvalidDataException("Active recovery identity is corrupt.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(accountBytes);
            CryptographicOperations.ZeroMemory(phraseBytes);
            if (!succeeded) CryptographicOperations.ZeroMemory(seed);
        }
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DecodeBase64Url(string encoded)
    {
        if (encoded.Contains('=') || string.IsNullOrEmpty(encoded)) throw new FormatException();
        var padding = (4 - encoded.Length % 4) % 4;
        return Convert.FromBase64String(
            encoded.Replace('-', '+').Replace('_', '/') + new string('=', padding));
    }
}
