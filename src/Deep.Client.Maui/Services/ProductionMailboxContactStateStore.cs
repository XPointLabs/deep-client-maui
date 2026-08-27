using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;

namespace Deep.Client.Maui.Services;

/// <summary>
/// Account-generation-scoped persistence for authenticated CMI1 values. Invitations contain
/// public route material, but remain inside the encrypted application database because they are
/// exchanged only through an authenticated contact channel.
/// </summary>
internal sealed class ProductionMailboxContactStateStore(
    IAtomicBoundedSettingsRepository settings)
{
    private const int MaximumEncodedBytes = 2 * 1024;
    private const int MaximumAttempts = 4;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IAtomicBoundedSettingsRepository settings =
        settings ?? throw new ArgumentNullException(nameof(settings));

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
        var encoded = Encode(invitation);
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
                        result = await settings.CreateAtomicBoundedSettingAsync(
                            key, encoded, MaximumEncodedBytes, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case AtomicBoundedSettingReadResult.Found when current.Revision is not null:
                        if (Fixed(current.GetValueCopy(), encoded)) return;
                        result = await settings.ReplaceAtomicBoundedSettingAsync(
                            key, current.Revision, encoded, MaximumEncodedBytes,
                            cancellationToken).ConfigureAwait(false);
                        break;
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
                        Fixed(observed.GetValueCopy(), encoded))
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
            AtomicBoundedSettingReadResult.Found => Decode(outcome.GetValueCopy()),
            AtomicBoundedSettingReadResult.Oversized => throw new InvalidDataException(
                "Persisted production contact invitation is oversized."),
            _ => throw new InvalidOperationException(
                "Production contact invitation storage is unavailable.")
        };
    }

    private static byte[] Encode(string invitation)
    {
        var output = JsonSerializer.SerializeToUtf8Bytes(new StoredInvitation(1, invitation));
        if (output.Length is 0 or > MaximumEncodedBytes)
            throw new InvalidDataException("Production contact invitation is oversized.");
        return output;
    }

    private static string Decode(byte[] encoded)
    {
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
            var properties = root.EnumerateObject().Select(static value => value.Name).ToArray();
            if (root.ValueKind != JsonValueKind.Object || properties.Length != 2 ||
                !properties.Contains("Schema", StringComparer.Ordinal) ||
                !properties.Contains("Invitation", StringComparer.Ordinal) ||
                root.GetProperty("Schema").GetInt32() != 1 ||
                root.GetProperty("Invitation").ValueKind != JsonValueKind.String)
                throw new InvalidDataException(
                    "Persisted production contact invitation is invalid.");
            var invitation = root.GetProperty("Invitation").GetString();
            if (string.IsNullOrWhiteSpace(invitation) ||
                StrictUtf8.GetByteCount(invitation) > MaximumEncodedBytes)
                throw new InvalidDataException(
                    "Persisted production contact invitation is invalid.");
            return invitation;
        }
        catch (Exception exception) when (exception is JsonException or
            InvalidOperationException or FormatException)
        {
            throw new InvalidDataException(
                "Persisted production contact invitation is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static string Key(SessionId local, SessionId? peer) =>
        "deep.mailbox.contact-invitation.v1:" + local.Value + ":" +
        (peer?.Value ?? "self");

    private static bool Fixed(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);

    private sealed record StoredInvitation(int Schema, string Invitation);
}
