using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Services;

/// <summary>
/// Account-generation-scoped persistence for authenticated CMI1 values. Invitations contain
/// public route material, but remain inside the encrypted application database because they are
/// exchanged only through an authenticated contact channel.
/// </summary>
internal sealed class ProductionMailboxContactStateStore(
    IAtomicBoundedSettingsRepository settings,
    TimeProvider? timeProvider = null)
{
    private const int HashLength = 32;
    private const int MaximumEncodedBytes = 2 * 1024;
    private const int MaximumAttempts = 4;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IAtomicBoundedSettingsRepository settings =
        settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public Task SaveLocalInvitationAsync(
        SessionId local,
        string invitation,
        CancellationToken cancellationToken = default) =>
        SaveAsync(Key(local, null), invitation, cancellationToken);

    public Task<string?> LoadLocalInvitationAsync(
        SessionId local,
        CancellationToken cancellationToken = default) =>
        LoadAsync(Key(local, null), cancellationToken);

    public Task SavePeerInvitationAsync(
        SessionId local,
        SessionId peer,
        string invitation,
        CancellationToken cancellationToken = default) =>
        SaveAsync(Key(local, peer), invitation, cancellationToken);

    public Task<string?> LoadPeerInvitationAsync(
        SessionId local,
        SessionId peer,
        CancellationToken cancellationToken = default) =>
        LoadAsync(Key(local, peer), cancellationToken);

    private async Task SaveAsync(
        string key,
        string invitation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invitation);
        if (StrictUtf8.GetByteCount(invitation) > MaximumEncodedBytes)
            throw new InvalidDataException("Production contact invitation is oversized.");

        StoredInvitationState? candidate = null;
        byte[]? encoded = null;
        try
        {
            for (var attempt = 0; attempt < MaximumAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await settings.ReadAtomicBoundedSettingAsync(
                    key, MaximumEncodedBytes, cancellationToken).ConfigureAwait(false);
                AtomicBoundedSettingMutationResult result;
                switch (current.Result)
                {
                    case AtomicBoundedSettingReadResult.Missing:
                        candidate ??= VerifyForWrite(invitation);
                        encoded ??= Encode(candidate);
                        result = await settings.CreateAtomicBoundedSettingAsync(
                            key, encoded, MaximumEncodedBytes, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case AtomicBoundedSettingReadResult.Found when current.Revision is not null:
                    {
                        var persisted = Decode(current.GetValueCopy());
                        if (string.Equals(
                                persisted.Invitation, invitation, StringComparison.Ordinal))
                            return;

                        candidate ??= VerifyForWrite(invitation);
                        EnsureMonotonicSuccessor(persisted, candidate);
                        encoded ??= Encode(candidate);
                        result = await settings.ReplaceAtomicBoundedSettingAsync(
                            key, current.Revision, encoded, MaximumEncodedBytes,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case AtomicBoundedSettingReadResult.Oversized:
                        throw new InvalidDataException(
                            "Persisted production contact invitation is oversized.");
                    default:
                        throw new InvalidOperationException(
                            "Production contact invitation storage is unavailable.");
                }

                if (result == AtomicBoundedSettingMutationResult.Applied) return;
                if (result is AtomicBoundedSettingMutationResult.Conflict or
                    AtomicBoundedSettingMutationResult.Missing)
                    continue;
                if (result == AtomicBoundedSettingMutationResult.OutcomeUnknown)
                {
                    var observed = await settings.ReadAtomicBoundedSettingAsync(
                        key, MaximumEncodedBytes, CancellationToken.None).ConfigureAwait(false);
                    if (observed.Result == AtomicBoundedSettingReadResult.Found &&
                        string.Equals(
                            Decode(observed.GetValueCopy()).Invitation,
                            invitation,
                            StringComparison.Ordinal))
                        return;
                }
                throw new InvalidOperationException(
                    "Production contact invitation did not persist atomically.");
            }
            throw new InvalidOperationException(
                "Production contact invitation changed concurrently.");
        }
        finally
        {
            if (encoded is not null)
                CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private async Task<string?> LoadAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var outcome = await settings.ReadAtomicBoundedSettingAsync(
            key, MaximumEncodedBytes, cancellationToken).ConfigureAwait(false);
        return outcome.Result switch
        {
            AtomicBoundedSettingReadResult.Missing => null,
            AtomicBoundedSettingReadResult.Found => Decode(outcome.GetValueCopy()).Invitation,
            AtomicBoundedSettingReadResult.Oversized => throw new InvalidDataException(
                "Persisted production contact invitation is oversized."),
            _ => throw new InvalidOperationException(
                "Production contact invitation storage is unavailable.")
        };
    }

    private StoredInvitationState VerifyForWrite(string invitation)
    {
        var verified = ContactMailboxInvitationService.ParseAndVerify(
            invitation, timeProvider);
        var canonicalInvitation = ContactMailboxInvitationService.DecodeCanonicalText(invitation);
        try
        {
            return new StoredInvitationState(
                invitation,
                verified.RouteSequence,
                verified.CanonicalRouteAdvertisementHash.ToArray(),
                verified.IssuedAtUnixSeconds,
                verified.ExpiresAtUnixSeconds,
                SHA256.HashData(canonicalInvitation));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalInvitation);
        }
    }

    private static void EnsureMonotonicSuccessor(
        StoredInvitationState current,
        StoredInvitationState candidate)
    {
        if (candidate.RouteSequence < current.RouteSequence)
            throw new InvalidDataException(
                "Production contact invitation route sequence cannot roll back.");
        if (candidate.RouteSequence == current.RouteSequence)
            throw new InvalidDataException(
                "Production contact invitation route sequence equivocation was detected.");
    }

    private static byte[] Encode(StoredInvitationState state)
    {
        var output = JsonSerializer.SerializeToUtf8Bytes(new StoredInvitation(
            2,
            state.Invitation,
            state.RouteSequence,
            Convert.ToHexString(state.CanonicalPra1Hash),
            state.IssuedAtUnixSeconds,
            state.ExpiresAtUnixSeconds,
            Convert.ToHexString(state.Cmi1Hash)));
        if (output.Length is 0 or > MaximumEncodedBytes)
        {
            CryptographicOperations.ZeroMemory(output);
            throw new InvalidDataException("Production contact invitation is oversized.");
        }
        return output;
    }

    private static StoredInvitationState Decode(byte[] encoded)
    {
        byte[]? canonicalInvitation = null;
        try
        {
            if (encoded.Length is 0 or > MaximumEncodedBytes)
                throw new InvalidDataException(
                    "Persisted production contact invitation is invalid.");
            using var document = JsonDocument.Parse(encoded, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(
                    "Persisted production contact invitation is invalid.");
            var expectedProperties = new HashSet<string>(StringComparer.Ordinal)
            {
                "Schema",
                "Invitation",
                "RouteSequence",
                "CanonicalPra1Hash",
                "IssuedAtUnixSeconds",
                "ExpiresAtUnixSeconds",
                "Cmi1Hash"
            };
            foreach (var property in root.EnumerateObject())
            {
                if (!expectedProperties.Remove(property.Name))
                    throw new InvalidDataException(
                        "Persisted production contact invitation is invalid.");
            }
            if (expectedProperties.Count != 0 || root.GetProperty("Schema").GetInt32() != 2 ||
                root.GetProperty("Invitation").ValueKind != JsonValueKind.String)
                throw new InvalidDataException(
                    "Persisted production contact invitation is invalid.");

            var invitation = root.GetProperty("Invitation").GetString();
            if (string.IsNullOrWhiteSpace(invitation) ||
                StrictUtf8.GetByteCount(invitation) > MaximumEncodedBytes)
                throw new InvalidDataException(
                    "Persisted production contact invitation is invalid.");
            var routeSequence = root.GetProperty("RouteSequence").GetUInt64();
            var issuedAt = root.GetProperty("IssuedAtUnixSeconds").GetUInt64();
            var expiresAt = root.GetProperty("ExpiresAtUnixSeconds").GetUInt64();
            var pra1Hash = ParseCanonicalHash(root.GetProperty("CanonicalPra1Hash"));
            var cmi1Hash = ParseCanonicalHash(root.GetProperty("Cmi1Hash"));

            var verified = ContactMailboxInvitationService.ParseAndVerify(
                invitation,
                new FixedTimeProvider(issuedAt),
                clockSkewSeconds: 0);
            canonicalInvitation = ContactMailboxInvitationService.DecodeCanonicalText(invitation);
            var computedCmi1Hash = SHA256.HashData(canonicalInvitation);
            if (verified.RouteSequence != routeSequence ||
                verified.IssuedAtUnixSeconds != issuedAt ||
                verified.ExpiresAtUnixSeconds != expiresAt ||
                !Fixed(verified.CanonicalRouteAdvertisementHash.Span, pra1Hash) ||
                !Fixed(computedCmi1Hash, cmi1Hash))
                throw new InvalidDataException(
                    "Persisted production contact invitation metadata is invalid.");

            return new StoredInvitationState(
                invitation,
                routeSequence,
                pra1Hash,
                issuedAt,
                expiresAt,
                cmi1Hash);
        }
        catch (Exception exception) when (exception is JsonException or
            InvalidOperationException or ArgumentException or FormatException or
            OverflowException or
            ContactMailboxInvitationException)
        {
            throw new InvalidDataException(
                "Persisted production contact invitation is invalid.", exception);
        }
        finally
        {
            if (canonicalInvitation is not null)
                CryptographicOperations.ZeroMemory(canonicalInvitation);
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static byte[] ParseCanonicalHash(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.String)
            throw new InvalidDataException(
                "Persisted production contact invitation hash is invalid.");
        var encoded = property.GetString();
        if (encoded is null || encoded.Length != HashLength * 2)
            throw new InvalidDataException(
                "Persisted production contact invitation hash is invalid.");
        var decoded = Convert.FromHexString(encoded);
        if (decoded.Length != HashLength ||
            !string.Equals(Convert.ToHexString(decoded), encoded, StringComparison.Ordinal))
            throw new InvalidDataException(
                "Persisted production contact invitation hash is invalid.");
        return decoded;
    }

    private static string Key(SessionId local, SessionId? peer) =>
        "deep.mailbox.contact-invitation.v1:" + local.Value + ":" +
        (peer?.Value ?? "self");

    private static bool Fixed(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);

    private sealed record StoredInvitation(
        int Schema,
        string Invitation,
        ulong RouteSequence,
        string CanonicalPra1Hash,
        ulong IssuedAtUnixSeconds,
        ulong ExpiresAtUnixSeconds,
        string Cmi1Hash);

    private sealed record StoredInvitationState(
        string Invitation,
        ulong RouteSequence,
        byte[] CanonicalPra1Hash,
        ulong IssuedAtUnixSeconds,
        ulong ExpiresAtUnixSeconds,
        byte[] Cmi1Hash);

    private sealed class FixedTimeProvider(ulong unixSeconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.FromUnixTimeSeconds(checked((long)unixSeconds));
    }
}
